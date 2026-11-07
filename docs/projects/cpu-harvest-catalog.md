# CPU harvest catalog (research draft 2, 2026-11-07)

This is the CPU twin of `apu-harvest-catalog.md` and `ppu-harvest-catalog.md`. It uses the same method: take the last build before a CPU-accuracy milestone, a console port, or an embedded port. All research was web-only. Nothing was downloaded or cloned, and files were viewed in a browser.

**How sure each entry is:**
- **[verified]**: the file was seen in a browser at the target, and the link is given.
- **[partial]**: some of it is sourced, and the gap is named.
- **[unverified]**: a lead only.

**Licence flags** are the same as in the APU catalog:
- **OK**: permissive or public domain.
- **EXC**: needs the foreign-core art exception (the FA-1 pattern in `docs/KNOWN_GAPS.md`).
- **??**: licence unknown.

**The user's rule for CPUs.** A CPU's jank only shows through the picture and the sound, and many CPU bugs just crash games. Candidates are ranked by how visible or audible the jank is without crashing common games. Timing jank ranks highest: scanline batching, wrong cycle tables, and early or late IRQs and NMIs. Opcode and flag bugs that hang games rank lowest.

**Main finding: the sync model is the character.** The visible CPU jank in old emulators comes mostly from how much CPU time runs before the PPU is allowed to look, not from opcode bugs. nofrendo's own source says so. Its MMC3 hack comment reads "The issue is that the CPU and PPU aren't running in sync", and the hack exists "to fix many MMC3 games with status bar vertical alignment issues" ([nes.c @05e97e8](https://github.com/ducalex/retro-go/blob/05e97e8/retro-core/components/nofrendo/nes/nes.c)). So most of the NES entries below can be one **scanline-batch layer over CPU_FIX** (see "How it would plug in"). Each one would be a preset of that layer, not a port of foreign opcode code.

## Ranked top 10 (visible/audible jank, then low crash risk, then ease)

| # | Prospect | CPU | Exact target | Licence | Status |
|---|---|---|---|---|---|
| 1 | **InfoNES sync**: 113 steps per line, sprite-0 hit faked by splitting the budget at the sprite's X | 6502 | [jay-kumogata/InfoNES](https://github.com/jay-kumogata/InfoNES) master (no tags), `src/InfoNES.cpp` 1191 lines, [`InfoNES.h`](https://raw.githubusercontent.com/jay-kumogata/InfoNES/master/src/InfoNES.h) `STEP_PER_SCANLINE 113`, `STEP_PER_FRAME 29828` | **Apache-2.0** ([LICENSE](https://raw.githubusercontent.com/jay-kumogata/InfoNES/master/LICENSE)). The readme calls it "freeware". **OK** | [verified] (master, not a tag) |
| 2 | **nofrendo sync**: whole-line batch, NMI 7 cycles after line 241, and `cycles_per_scanline += 2.5` for 4 CRCs | 6502 | retro-go `05e97e8`: [`nofrendo/nes/nes.c`](https://github.com/ducalex/retro-go/blob/05e97e8/retro-core/components/nofrendo/nes/nes.c) (314 lines) + [`nes/cpu.c`](https://github.com/ducalex/retro-go/blob/05e97e8/retro-core/components/nofrendo/nes/cpu.c) (1551 lines) | LGPL v2, **EXC** | [verified] |
| 3 | **PocketNES (GBA)**: cycle budget packed into the flags register, plus an idle-loop speed hack at branches and JMP | 6502 | [catskull/pocketnes](https://github.com/catskull/pocketnes) (archive.org mirror, last updated 2013-07-01): [`src/6502.s`](https://github.com/catskull/pocketnes/blob/master/src/6502.s) 1752 lines, [`src/new_speed_hack.c`](https://github.com/catskull/pocketnes/blob/master/src/new_speed_hack.c) 494 lines | None in the files or repo, **??** | [verified] files / [partial] licence |
| 4 | **gnuboy SM83**: no cycles charged for interrupt dispatch, HALT jumps to the next event | SM83 | [rofl0r/gnuboy `cpu.c`](https://github.com/rofl0r/gnuboy/blob/master/cpu.c) (935 lines; master, the pre-milestone tag not pinned) | GPL-2 (project), **EXC** | [partial] |
| 5 | **Snes9x 1.43 main loop**: a flat `MemSpeed` per opcode step, a fixed 3-cycle IRQ delay, and the H-IRQ as a per-line event | 65816 | [TASVideos/snes9x-rr branch `snes9x-143`](https://github.com/TASVideos/snes9x-rr/tree/snes9x-143), [`cpuexec.cpp`](https://github.com/TASVideos/snes9x-rr/blob/snes9x-143/cpuexec.cpp) 458 lines | "Freeware for PERSONAL USE only" (header), **EXC** | [verified] |
| 6 | **FCEUX before the new-PPU sync fix**: the `cpu_ignore` commit `e0028f6` (2009-06-24), reverted the next day in `854553e` | 6502 | [commit e0028f6](https://github.com/TASEmulators/fceux/commit/e0028f6) on `src/x6502.cpp` + `src/ppu.cpp` | GPL-2, **EXC** | [verified] commit / [partial] effect |
| 7 | **DOSBox 0.74 `core_simple`**: `while (CPU_Cycles-->0)`, one "cycle" per instruction, lazy flags | x86 | SVN [`tags/RELEASE_0_74/src/cpu/core_simple.cpp`](https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_74/src/cpu/core_simple.cpp) r3548, 207 lines; [`lazyflags.h`](https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_74/src/cpu/lazyflags.h) 133 lines | GPL-2+, **EXC** | [verified] |
| 8 | **DOSBox 0.74 `core_dyn_x86`**: cycles checked as a register during a translated block | x86 | [`core_dyn_x86.cpp`](https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_74/src/cpu/core_dyn_x86.cpp) r3548, 480 lines | GPL-2+, **EXC** | [partial]: whether it decrements per block or per instruction was not shown |
| 9 | **Musashi 3.3 / 3.4** (2001-2002): busy-wait branch speedup in 3.3, no prefetch by default | 68000 | history via [applefritter copy of history.txt](https://git.applefritter.com/Macintosh-HW/minimacplus/src/commit/7cb34b257d7d49d730280cbd02778d5e7f76dbfb/components/tme/musashi/history.txt) | Pre-2013 Musashi licence; MIT from 2013-07-15 per the history, **??/EXC** at old tags, **OK** for MIT versions | [partial] |
| 10 | **ZSNES 65816**: 16-bit instructions are not charged their extra cycle | 65816 | Documented by ZSNES itself in [Getting Support](https://zsnes-docs.sourceforge.net/html/support.htm). Source not pinned. | GPL-2 (ZSNES), **EXC** | [partial] |

### Changes from draft 1
- **InfoNES's licence is Apache-2.0.** It is the one clean NES timing donor besides nesDS, and it stays #1.
- **PocketNES is pinned.** Its `6502.s` has `fetch N` macros with inline page-cross subtracts, IRQ checks only on PLP/RTI/CLI, and branch/JMP speedhack hooks (`speedhack_pc`, `dobranchhack`). `new_speed_hack.c` finds tight loops and skips through them using a computed `divider`.
- **Snes9x 1.43 is opened and pinned.** The [1.53 notes](https://github.com/snes9xgit/snes9x/releases?page=2) "rebuilt IRQ handling" and dropped the CPUShutdown speed hack, so everything up to 1.52 sits before that milestone.
- **DOSBox 0.74 is opened** through the SourceForge SVN web view.
- **nesDS dropped out of the top 10.** Its `cpu.s` was not read. It is probably PocketNES-lineage, which makes it a duplicate of the batch layer.
- **PicoDrive DrZ80/Cyclone dropped.** Only the folder listing was seen, and it showed no licences.
- **FCEUX now has a real commit.** In `e0028f6`, the page-cross and IRQ cycles were made to "count twice" against the new PPU. As read, the `runppu` branch zeroes `cpu_ignore` before it subtracts, so the CPU still ran the full `x` cycles; the commit was reverted the next day.

## NES 6502 cores

### InfoNES (#1) [verified]
- **What it does:** each line, `InfoNES_Cycle()` runs 113 steps (real hardware is 113.667). On a sprite-0 line, it runs `SPRRAM[SPR_X]*113/256` steps, sets the hit flag, then runs the rest.
- **Jank:**
  - About 2 cycles of drift every 3 lines. Over a 262-line frame that is about 175 cycles, which the frame boundary absorbs (29828 against the real ~29780.5).
  - Sprite-0 splits land at a guessed horizontal position.
  - Mid-line `$2005/$2006` writes apply at line granularity.
  - What players see: status-bar seams in sprite-0 games wobble and timed rasters drift. There is no sourced game report for InfoNES itself. The [nesdev forum thread on SMB3 split artifacts](https://forums.nesdev.org/viewtopic.php?t=5811) is general background only.
- **Crash risk:** low as a batch-layer preset. Games that poll `$2002` for sprite 0 in a tight loop still work, because the hit is set mid-budget.
- **Ports:** the RP2040 ports inherit it. pico-infonesPlus says it is built on Takano's [pico-infones](https://github.com/shuichitakano/pico-infones) ([opensensor mirror](https://github.com/opensensor/pico-infonesPlus)). The `InfoNES.h` path in pico-infones gave a 404, so its constant is unchecked. **[partial]**

### nofrendo (#2) [verified]
- **Sync (`nes.c`):** per line, add `cycles_per_scanline` and draw the line with `ppu_scanline()`. At line 241, run 7 cycles, then the NMI. The mapper `hblank` hook runs each line. Then the CPU runs its whole budget, or `timer_period` chunks if a mapper timer is set.
- **Sourced effect:** the 2.5-cycle hack is gated on CRCs for Zen Intergalactic, Super Mario Bros 3, Kirby's Adventure and Power Blade 2, with the comment about "status bar vertical alignment issues". Unhacked MMC3 games show the misaligned status bar; the hacked four show a different, tuned offset.
- **CPU (`cpu.c`):**
  - It has page-cross penalties (`PAGE_CROSS_CHECK`, indexed reads plus taken branches).
  - It has no dummy reads (`EMPTY_READ` is empty).
  - CLI and RTI take a pending IRQ at once, with no one-instruction delay.
  - It has the unofficial opcodes, and JAM halts.
  - These are small effects. Missing dummy reads only matter for `$2007` and `$4015` read side effects (rare).
- **Crash risk:** low. CLI with an instant IRQ can change IRQ-heavy games slightly.
- We already harvested NOFR as an APU, so the pairing is natural.

### PocketNES (#3) [verified files, ?? licence]
- **What it does:** cycles and three flag bits share one ARM register. Base costs come from the `fetch N` macros, and taken branches that cross a page subtract 3 more. IRQ is only re-checked on flag-changing ops.
- **Speed hack:** a detected idle loop (a backward branch with bounded memory access) is skipped by computing the iterations left. `game_specific_hack` matches Capcom and Konami wait loops. The scan reruns every 8 frames.
- **Jank:** the loop skip shifts everything after a wait loop. Sprite-0 and timer waits finish "instantly" at the next event, so splits snap and music driven from the main loop can change tempo.
- **Port:** rebuild it from these documented rules in C#; do not transliterate the ARM. The speed-hack finder (494 lines of C) is directly portable.
- **No game sourced.** [GameBrew](https://www.gamebrew.org/wiki/PocketNES_GBA) mentions a "Version 7a Compatibility List" that was not retrieved.

### nesDS [partial]
[NesDS v2.0.1 `arm9/source`](https://github.com/DS-Homebrew/NesDS/tree/v2.0.1/arm9/source) has `cpu.s` (ARM asm) and an `RP2A03` submodule from FluBBaOfWard/N2A03. It is public domain per the APU catalog. I did not read `cpu.s`. Its likely lineage is PocketNES or FluBBa, so treat it as a PocketNES-family preset until the file is read.

### FCEU/FCEUX (#6)
- **Changelog** ([FCEUX changelog](https://github.com/TASEmulators/fceux/blob/master/changelog.txt)):
  - 2.1.1 (2009-06): ATX `$EE->$FF`; the `$4017` IRQ inhibit fixed, then reverted because it broke Dragon Warrior 4.
  - 2.1.x: qeed's new PPU.
  - 2.2.1: the BizHawk PPU import.
- **Commits** ([x6502.cpp history](https://github.com/TASEmulators/fceux/commits/master/src/x6502.cpp)):
  - `e0028f6` / `854553e` (2009-06): `cpu_ignore`.
  - `8166a43` (2012-10-30): power-on SP `$FD`.
  - `4c28daf` (2023-07-14): writes update the data bus.
  - No dummy-read commit is named.
- **Best target:** the old-PPU line scheduling before 2.1.1. This duplicates the batch layer; the ATX and `$4017` quirks are the only CPU-specific flavour.
- **Line count at the pre-2.1.1 tag:** not pinned. **[partial]**

### Not good donors (researched, low jank)
- **Nestopia and Nintendulator:** cycle-granular CPUs ([Mankier: Nestopia](https://mankier.com/package/nestopia); [emulator-zone: Nintendulator](https://emulator-zone.com/doc.php/nes/nintendulator.html), PPU first, then the CPU). There is no pre-milestone version with a public source tag found.
- **Early Mesen:** the [SourMesen/Mesen](https://github.com/SourMesen/Mesen) releases page shows none (archived 2024-12-18). Mesen was cycle-based from its public start, so it is not a donor. **[partial]**
- **loopyNES:** only appears in emulator lists; no source found ([history blog](https://historyofvideogameemulators.blogspot.com/2023/07/the-history-of-nes-emulators-part-6.html)). **[unverified]**
- **VirtuaNES:** a GitHub mirror was referenced but `cpu.cpp` was not found. **[unverified]** It is scanline-based with per-game hacks (a lead only) and fits as a batch preset.
- **fake6502 and MAME m6502:** the fake6502 source page did not load (a sign-in wall). **[unverified]** It is instruction-granular, so it would add little beyond batch presets.
- **himal-mandalia/nes-emu** ([NES.cpp](https://github.com/himal-mandalia/nes-emu/blob/master/NES.cpp)) has the comment "cpu_cycles_per_scanline = 113 while the real value is about 113.67". It shows the InfoNES pattern is common.

## Foreign CPUs (mixed onto NES and their own consoles)
- **SM83 / gnuboy (#4):**
  - Per-instruction catch-up, interrupt dispatch adds 0 cycles (hardware takes 20), and HALT idles to the next event.
  - EI is delayed by one instruction (correct).
  - On a Game Boy core, STAT/LYC raster handlers start 20 cycles early, giving wavy splits in games relying on [LYC handler timing](https://gbdev.io/guides/lyc_timing.html).
  - Sourced fragile games: Pinball Deluxe and Pinball Fantasies (STAT dispatch timing) and Alone in the Dark (STAT blocking), per the [GBdev tricky games list](https://gbdev.gg8.se/wiki/articles/Tricky-to-emulate_games). mGBA [notes](https://mgba.io/2017/05/29/holy-grail-bugs/) that Pinball Fantasies runs on inaccurate emulators and crashes on accurate ones: a jank-friendly case.
  - The gnuboy WHATSNEW fork page (with an Alleyway late-line fix) was blocked by a bot check.
- **SM83 / VBA:** a speedrun.com [informal test](https://www.speedrun.com/es-ES/ffl2/forums/wux39) found VBA 1.8.0 about 242 frames faster over 7:13 than bgb or Gambatte (lag not emulated). The source was not pinned. **[partial]**
- **SM83 / Gambatte and SameBoy:** both are accuracy-first from early on (Gambatte is GPL-2, [libretro readme](https://github.com/libretro/gambatte-libretro)). Poor donors.
- **65816 / Snes9x 1.43 (#5):**
  - `S9xMainLoop` adds `CPU.MemSpeed` per opcode step and then runs it. `S9xSetIRQ` sets `IRQCycleCount = 3`. H-timer IRQs are line events (`HTIMER_BEFORE/AFTER_EVENT`).
  - On a NES board, the flat cost per step acts like a cycle-table detune.
  - The [1.53 release](https://github.com/snes9xgit/snes9x/releases?page=2) "rebuilt IRQ handling", so 1.43 is pre-milestone.
  - Sourced games: none specific; [TASVideos forum](https://tasvideos.org/Forum/Topics/8739) users disagree on 1.43 timing (anecdotal).
- **65816 / ZSNES (#10):** the docs admit "65816 lacks correct timing" and that 16-bit ops don't deduct their extra cycle ([support page](https://zsnes-docs.sourceforge.net/html/support.htm)). It is x86 asm, so rebuild it as a CPU_SNES cycle-table knob.
- **65816 / bsnes:** cycle-accurate from early on. Not researched further.
- **Z80 / 68K:**
  - Musashi (#9): 3.3 added busy-wait branch skipping (audible on DAC loops), 3.4 added undocumented behaviour, MIT from 2013. Exact files at the 3.3 tag are not pinned.
  - [PicoDrive `cpu/`](https://github.com/notaz/picodrive/tree/master/cpu) holds cyclone (a submodule, irixxxx/cyclone68000 `3ac7cf1`), DrZ80, cz80, fame and musashi. No licences were shown on that page.
  - Gens history was not researched.

## On other consoles
- PocketNES (GBA, #3) and nesDS (DS) are ARM asm 6502s. Rebuild their rules; don't port the asm.
- PicoDrive's Cyclone and DrZ80 are ARM asm. FAME is C/asm.
- PSP and Dreamcast NES ports: not researched (gap).

## Embedded
- retro-go nofrendo (ESP32, #2): the hack table above is the clearest sourced evidence of batch jank in the whole catalog.
- RP2040 InfoNES lineage: [pico-infonesPlus](https://github.com/opensensor/pico-infonesPlus) overclocks the Pico to keep up, and inherits InfoNES's 113 steps per line (constant not seen in the port).

## x86 (separate situation)
DOSBox 0.74's core files are pinned (#7, #8). [`core_simple.cpp`](https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_74/src/cpu/core_simple.cpp) counts one "cycle" per instruction, so the "cycles" setting is instructions per ms. Every instruction costs the same, so a DIV-heavy loop runs as fast as a NOP loop.

The effect is wrong game speed and wrong delays in calibrated delay loops: PC-speaker music that is timed by delay loops rather than the PIT changes tempo and pitch. On the x86 console this is a speed/pitch climb, not a raster one.

The tag also has `core_normal`, `core_full`, `core_prefetch` and `core_dynrec`. The prefetch queue was added r3158 (2008-05-21), and `callback.cpp` r3589 added "basic delay for a few DOS games". PCem v12 (2017) added basic 286/386 prefetch ([notes](https://virtuallyfun.com/2017/02/19/pcem-v12-released/)). The 86Box timing history was not pinned. **[partial]**

## How it would plug in (repo)

**Contract.** Every NES CPU implements `ICPU` (`Windows/NesEmulator/cpus/ICPU.cs`): `ExecuteInstruction()` returns cycles, plus `RequestIRQ/RequestNMI`, `GetState/SetState` and `GetRegisters`. `board/NES.cs` calls `ExecuteInstruction()` and then steps the PPU by `cycles*3` and the APU by `cycles`. This happens per instruction on the precise paths, or after `FlushBatch` on the heuristic and event paths.

**Foreign walls.** `mix/CpuWalls.cs` (276 lines) holds `CPU_Z80` and `CPU_M68K` as adapter boards: clock ratio carry, vector overlays and IRQ latching. They save through `BlobCpuState.Make/TryLoad`, so a state reloads exactly on its own core and other cores get a 6502 register view. `mix/CPU_SNES.cs` and `mix/GbCpuMix.cs` are the 65816 and SM83 bridges.

**Scanline-batch layer (proposed `CPU_BAT`, "Batch"):**
- **Shape:** an `ICPU` that owns a private `CPU_FIX` instance. Each `ExecuteInstruction()` runs inner instructions until the line budget is spent, then returns the summed cycles. `NES.cs` then catches up the PPU and APU in one go, so `$2002`, sprite-0 and MMC3 A12 timing are seen at line granularity. No change to `NES.cs` is needed.
- **Knobs:**
  - `CyclesPerLine` (113 InfoNES / 113.667 / nofrendo's computed value), with a fractional accumulator option.
  - `NmiDelay` (0 / 7).
  - `Sprite0Split` (off / InfoNES X-split).
  - `PerGameLineHack` (nofrendo's +2.5 on the four CRCs).
  - `IdleLoopSkip` (PocketNES finder).
  - `IrqCheck` (every instruction / at line end / only on PLP/RTI/CLI).
- **Presets:** "InfoNES", "nofrendo", "PocketNES" and "FCEU-old", each a named core in CoreRegistry ("BAT-INF", "BAT-NOF", ...). The menu says plainly that it is FIX opcodes on a foreign scheduler. Nothing is swapped silently, and there is no fallback to FIX if a game breaks (feedback_no_silent_fallbacks).
- **Traps:**
  - The precise paths catch up a FIX PPU per instruction (`bus.PpuCaughtUpPerInstruction`, `PreciseSteppingActive`, `MarkInstructionStart`). If the bus catches up the PPU on register reads, it would erase the jank. The layer must declare itself non-precise, or the bus must honour a "batched CPU" flag. Check this before building.
  - The DMC DMA stall model also assumes per-instruction accounting.
  - The event-scheduler path casts `bus.cpu as CPU_LOW`. Make sure the wrapper does not hit that null path.
- **State:** `GetState` returns `BlobCpuState.Make("BAT-...", w => { inner FIX state, line accumulator, sprite-0 progress, idle-loop table })`. `SetState` uses `TryLoad`, so a save loads exactly on its own preset. A foreign state loads its 6502 register view into the inner FIX and resets the accumulator, which matters for butchering-mode cross-core reloads and the fuzz undo checks in the test battery.
- **Other donors:**
  - gnuboy: knobs on the GB core's dispatch cost and HALT skip.
  - Snes9x and ZSNES: knobs on `CPU_SNES` cycle accounting (flat MemSpeed, missing 16-bit penalty, IRQ delay 3).
  - DOSBox simple: a knob on the x86 core ("1 cycle per instruction").
  - These are "character knobs" on existing cores, so they need no foreign opcode ports and fall under the licence note in the next section.

## Licence notes
- **OK:** InfoNES (Apache-2.0), nesDS (PD), modern Musashi (MIT).
- **EXC:** nofrendo (LGPL-2), gnuboy, FCEUX, DOSBox and ZSNES (GPL-2), Snes9x 1.43 (non-commercial).
- **??:** PocketNES (no licence file in the mirror).
- Rebuilding a timing rule from a documented shortcut copies no code. Still, credit the source in `THIRD_PARTY_NOTICES.md` as an inspiration reference.

## Open gaps
- InfoNES and gnuboy are pinned at master, not at a historic tag. InfoNES has no tags.
- nesDS `cpu.s`, the VirtuaNES source, fake6502, MAME m6502 history, and the Gens/FAME/Cyclone licences.
- The pico-infones constant, and the PSP and Dreamcast ports.
- The 86Box timing history, and the decrement granularity in `core_dyn_x86`.
- Game-specific sourced reports for InfoNES and PocketNES wobble (only nofrendo's in-code list is sourced). The PocketNES 7a compatibility list was not retrieved.
- The precise-path PPU catch-up question in `NES.cs` (code check, not web).

## Climb plan (approved 2026-11-07)

**Check first, answered (code read of `board/NES.cs` and `board/Bus.cs`).** A batch wrapper alone would NOT batch on the default cores: with PPU_FIX (`IPpuFixTiming`) `RunFrame` takes the precise path, which opens a `BeginPreciseWindow()` around every `ExecuteInstruction()`, and inside that window each bus access steps the PPU and APU by one cycle. A wrapper that runs 113 cycles of inner instructions inside one call would still have the PPU caught up on every access, so the jank would vanish. The same flags gate the `$2002` extra-dot read (`Bus.cs:660`), PPU_FIX's data-port collision model (`PpuCaughtUpPerInstruction`) and the DMC DMA halt (`RequestDmcDma`: precise = halt on a read, otherwise a lump stall flushed by `FlushBatch`, which is what a batching emulator does anyway).
- **Design:** a marker interface `IBatchedCpu`. In `RunFrame` (heuristic path) and the two other places that derive the flags (the idle-stepping helper and the stepping path), a batched CPU forces `perInstruction`, `precisePpu` and the DMC predictive split off, so the wrapper's whole line is flushed in one `FlushBatch`. For every existing core the test is false, so the code they run is unchanged (proved by the golden suite).
- **Event scheduler:** `(bus.cpu as CPU_LOW)!` already throws for every non-LOW CPU (FIX too); it is off by default and the batch cores don't touch it.
- **Reset sequence and OAM DMA parity** test `cpu is CPU_FIX`; the wrapper is not, so both stay off for it (correct for a batching emulator).

**Steps**
1. `CPU_BAT` base: an `ICPU` + `IBatchedCpu` around a private `CPU_FIX`. Knobs: cycles per line (+ fraction carry), line phase locked at the vblank NMI, NMI delay, InfoNES sprite-0 split (needs a PPU with `IPpuViewProbe`; PPU_FIX has it), nofrendo per-game +2.5 (CRC32 list from `nes.c`), PocketNES-style idle-loop skip (rebuilt rule, not the finder), IRQ check points (every instruction / at a line or split start only). Presets as cores: `BAT_INES`, `BAT_NOFR`, `BAT_PNES`, `BAT_FCEU`. State: `BlobCpuState` "BAT_*".
2. Character options on foreign / other-console CPUs where clean: gnuboy (SM83), Snes9x 1.43 / ZSNES (65816), Musashi busy-wait (68000).
3. x86: DOSBox 0.74 core_simple (one cycle per instruction) if the x86 CPU plumbing allows.

**Progress**

| Step | Core | State | Commit |
|---|---|---|---|
| 0 | catalog + plan | done | this commit |
| 1 | BAT_INES / BAT_NOFR / BAT_PNES / BAT_FCEU | done: Kirby's status panel 1 line high + stray tiles, Zelda II title split moves, Castlevania III differs; PPU_FIX applies the $2006 copy at once under a batch CPU | step 1 commit |
| 2a | SM83 gnuboy timing (GB-GNUB: free interrupt dispatch) | done, picture unchanged in Zelda LA / Pinball Fantasies / Road Rash / Kirby Pinball, audio length moves; HALT jump is already what the stock CPU does | step 2 commit |
| 2b | 65816 Snes9x 1.43 / ZSNES | skipped: CPU_SFC is clocked per bus access by BOARD_SFC, so a flat MemSpeed or a missing 16-bit cycle needs untimed accesses through the whole core (not a clean knob) | |
| 2c | 68000 Musashi busy-wait | skipped: the 3.3 source is not pinned (catalog gap) | |
| 3 | x86 I386_D074 (DOSBox 0.74 default cycles=3000) | done: our I386 already counts one cycle per instruction like core_simple, so the option is DOSBox's default speed (3 M instr/s vs 60) | step 2 commit |
