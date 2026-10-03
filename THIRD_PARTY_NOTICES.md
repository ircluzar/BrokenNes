# Third-party notices and provenance

BrokenNes is released under the **Digital Lifeform License 1.1** (`LICENSE.txt`). This file lists what in the repository, or in the programs built from it,
came from somewhere else, and under which terms. It is the root of the ledgers: each console family keeps its own, more detailed one beside its code
(`Windows/NesEmulator/gb/THIRD_PARTY_NOTICES.md`, `Windows/NesEmulator/snes/THIRD_PARTY_NOTICES.md`, `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`).
The plugin's About window and the Lite site point here.

**Provenance of changes.** The license asks that modifications be traceable to the digital agent that made them. The record is the git history: commit
trailers (`Co-Authored-By:`) name the agent, and commit messages say what changed and why. Notices below were written when the code was added; if you
find a borrowed piece that is not listed, that is a bug in this file: report it.

## Policy for what may be borrowed

- **MIT, BSD, ISC and zlib** code may be ported or adapted, with its copyright notice kept in the file's ledger entry (and in a header comment for
  substantial ports) and the license text shipped with the program (section 5).
- **GPL, AGPL, LGPL and non-commercial** code is never copied into BrokenNes. Such programs (Mesen 2, BlastEm, Genesis Plus GX, jgenesis, the Nuked-*
  cores, ...) are used only as **black-box oracles**: the program is run and its output is compared, its source is not read for the code we write.
  The Digital Lifeform License forbids further restrictions on the work and grants a right to sell, which copyleft and non-commercial terms conflict with.
- The exceptions that already exist are listed in section 2 and are meant to be replaced.
- Game ROMs and BIOS/firmware are never committed or shipped. Test ROMs are committed only when their license allows it (section 3).

## 1. Code derived from other projects (permissive)

| What | Where | Source and license |
|---|---|---|
| Mapper 30 (UNROM 512), ported | `Windows/NesEmulator/mappers/Mapper30.cs` | BizHawk `Mapper030.cs`, MIT, Copyright (c) BizHawk team |
| Mapper 5 (MMC5), adapted | `Windows/NesEmulator/mappers/Mapper5.cs` | BizHawk ExROM, MIT, Copyright (c) BizHawk team |
| MMC5 expansion audio, adapted | `Windows/NesEmulator/expansion/MMC5Audio.cs` | BizHawk `MMC5Audio`, MIT, Copyright (c) BizHawk team |
| Mapper 90 and Mapper 228 logic follows BizHawk's, simplified | `Windows/NesEmulator/mappers/Mapper90.cs`, `Mapper228.cs` | BizHawk, MIT, Copyright (c) BizHawk team |
| Sega: Z80 and others, **planned** | see `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` | each entry is added to that ledger when the code lands |

BizHawk's license statement (`LICENSE` in its repository) says the BizHawk team's own C# is provided under the MIT License, and that other parts of that repository
carry their own, sometimes incompatible, licenses. Only files authored by the BizHawk team are used here. The MIT text is reproduced in section 6.

## 2. Code with provenance that needs attention

| What | Where | Status |
|---|---|---|
| **QuickNES-family sound cores** (`APU_QN`, `APU_QLQ`, `APU_QLQ2`, `APU_QLOW`) | `Windows/NesEmulator/apus/APU_QN.cs`, `APU_QLQ.cs`, `APU_QLQ2.cs`, `APU_QLOW.cs` | The headers say "QuickNES-inspired ... informed by publicly known NES APU behavior and the QuickNES approach", while `APU_QN`'s description says "Retrofitted QuickNES APU from C++ to C#", and `readme.txt` credits "QN Cores: QuickNES Emulator". QuickNES is by blargg (Shay Green), distributed under the LGPL (not re-verified in this pass). **Until the cores are compared with the original, treat them as LGPL-derived.** They are "Unstable"-category cores, not defaults. To resolve: compare against the original, then either record the exact derivation here or rewrite them from documentation. |
| **NET-NES** by BotRandomness (the base emulator the project started from; `CPU_FMC`, `APU_FMC` are "loosely based" on it) | `Windows/NesEmulator/cpus/CPU_FMC.cs`, `apus/APU_FMC.cs`; credited in `readme.txt` | License not recorded in this repository: to be confirmed and entered here. |
| **S-DSP** ported from blargg's `snes_spc` (LGPL 2.1 or later) | `Windows/NesEmulator/snes/DSP_SFC.cs` | A documented **LGPL exception**, kept for now (decision of 2026-10-03). The SFC ledger plans a from-scratch replacement written without that source; the current core stays as the reference. |
| SPC700 cycle table (hardware timing data, copied) | `Windows/NesEmulator/snes/SMP_SFC.cs` | From snes9x (non-commercial / GPL lineage); the data is hardware timing. Details in the SFC ledger. |
| CGB compatibility palette tables (data) | `Windows/NesEmulator/gb/GbCompatPalettes.cs` | Transcribed from ISSOtm's `gb-bootroms` disassembly; tables only. Details in the GB ledger. |

## 3. Test material in the repository

| What | Where | License |
|---|---|---|
| gilyon/snes-tests v1.4 (SNES CPU and SPC700 test ROMs) | `Windows/Resources/snes-test-roms/` (committed, with `LICENSE` and `SOURCE.txt`) | MIT |
| c-sp/game-boy-test-roms v7.0 (a collection with mixed licenses) | `Windows/Resources/gb-test-roms/` (**not committed**, excluded through `.git/info/exclude`) | per-ROM licenses; kept local |
| Sega test material | not in the repository | MIT sets (z80test, SingleStepTests) may be committed later with `LICENSE` and `SOURCE.txt`; unlicensed ROMs and GPL test programs stay local |

## 4. Programs used only as references (nothing copied)

Mesen 2 (GPL-3.0), BizHawk's native cores, BlastEm (GPL-3.0), Genesis Plus GX (non-commercial), jgenesis (GPL-3.0), the Nuked-OPN2 / OPLL / MD / PSG cores
(LGPL-2.1 / GPL-2.0), Emulicious. They are run, and their output is compared; none of their source is included. Where a ledger says a source was read, the entry says so.

## 5. Packages that ship with the programs

| Package | Used by | License |
|---|---|---|
| NAudio 2.2.1 (and NAudio.Core, .Wasapi, .WinMM, .Midi) | desktop audio and MIDI | MIT, Copyright 2020 Mark Heath and contributors |
| MeltySynth 2.4.x | SoundFont cores (desktop) | MIT; its license file also carries the notices of C# Synth (Alex Veltsistas, 2014) and TinySoundFont |
| SharpDX 4.2.0 (Direct2D1, Direct3D11, DXGI, D3DCompiler, Mathematics, XInput) | desktop rendering and gamepads | MIT (license at sharpdx.org/License.txt), Copyright Alexandre Mutel |
| Microsoft.Web.WebView2 | desktop web modules | Microsoft license shipped in the package (`LICENSE.txt`: redistribution with the copyright notice kept) |
| Microsoft.AspNetCore.Components.WebAssembly 10.x | Lite (Blazor WebAssembly) | MIT, Microsoft |

Web assets of the Lite site and the desktop web modules (CSS, fonts, JavaScript libraries) are **not yet itemised here**; that audit is open.

## 6. License texts

**MIT License, Copyright (c) BizHawk team**

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR
A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The licenses of the packages in section 5 ship inside the packages (their `license` files) and are included with the distributed programs.
