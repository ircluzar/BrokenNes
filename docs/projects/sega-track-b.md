# Sega track B: Genesis / Mega Drive, milestone B1 (record)

Branch `worktree-agent-a2a300345dd9756b5`, written 2026-10-03 against `docs/projects/sega-tracks-brief.md`. B1 is the 68000, the master-clock board, the VDP's memories, ports, FIFO, DMA, counters
and interrupts, the pads, the cartridge and a Z80/PSG/YM2612-register sound unit. **There is no picture renderer and no FM audio yet** (B2), and `Consoles.GenesisReady` stays false: the
console is still the labelled placeholder in the desktop app.

## What is where

| Piece | Files | Notes |
|---|---|---|
| 68000 | `Windows/NesEmulator/md/M68k*.cs`, `IM68kBus.cs` | `M68k<TBus>` ported from ares (ISC), bus cycles timestamped; address error, trace, STOP, TAS switch. Ledger: `sega/THIRD_PARTY_NOTICES.md` 1b |
| Board | `md/BOARD_MD.cs` | one master clock; 68000 = master/7, Z80 and PSG = master/15; map, open bus, refresh, bank-window stalls, DMA arbitration; `ReadTrace`/`WriteTrace`/`InstructionHook` for traces |
| VDP | `md/MdVdp.cs` | registers, VRAM/CRAM/VSRAM, command protocol with pending flag, FIFO ring, DMA (bus, fill, copy), slot table as data, status, H/V counters and latch, VINT/HINT, H32/H40, V28/V30, PAL; debug views (`RenderDebugView`, `RenderPlaneDump`) |
| Pads | `md/MdPads.cs` | 3- and 6-button, TH phases counted in falling edges, 8192-cycle timeout, version register |
| Cartridge | `md/MdCartridge.cs` | header, SRAM with the `$A130F1` toggle and write protect, SSF2 banks above 4 MB; `MdSaves\` is the session's (`MdSession`) |
| Sound unit | `md/MdSoundUnit.cs` | the Z80 (shared core), BUSREQ/RESET handshake, bank register, bank-window stalls, PSG, YM2612 registers and timers (FM silent); ids `MD`, `MD-STUB` |
| Session | `md/MdSession.cs`, `systems/ConsoleSessions.cs` | built only when `Consoles.GenesisReady` |
| Workshop | `Workshop/Sega/M68kTestCli.cs` (+Vectors, SelfTest, Bench), `MdBoardTest.cs`, `MdRunCli.cs` | `--m68ktest`, `--md-test`, `--md-run` |

Shared files touched, all small and additive: `Windows/NesEmulator/sega/SegaSoundFactories.cs` (ids `MD` and `MD-STUB`), `Windows/NesEmulator/systems/ConsoleSessions.cs` (one line: the Genesis
session when ready), `Workshop/Program.cs` (three routes), `Workshop/Sega/SegaContractsTestCli.cs` (the MD factory assertions), `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` and the root
`THIRD_PARTY_NOTICES.md` (ares row and ISC text).

## Verification

| What | Result |
|---|---|
| `--m68ktest` SingleStepTests 680x0 + m68000 | 1,317,560 vectors: 1,284,785 pass, 32,775 excluded (eight classes the MAME set contradicts, listed in the ledger), **0 fail**; m68000 (MAME) 317,500 of 317,500; 8.2 s on 4 jobs |
| `--m68ktest --selftest` | 44 of 44 |
| `--m68ktest --bench` | 36-52 M instructions/s, 420-770 M clocks/s = 55-100x real time (core alone) |
| `--md-test` (new) | 113 of 113: map and open bus, version register, pads (3-button, the eight 6-button phases, the timeout), Z80 handshake with a real Z80 program and the bank window, VDP ports, FIFO and the unused bits (Nemesis tests 1, 5, 6), counters and flags, VINT/HINT, DMA and its timing, fill/copy details, HV latch, 68000/Z80 timing, cartridge and SRAM, state round trip |
| `--sega-contracts-test`, `--z80test --selftest`, `--sega-test` | 78 of 78, 57 of 57, 77 of 77 |
| Desktop smoke, Sega preview smoke | 9 of 9, 25 of 25 (Genesis is still the placeholder) |
| WebLite | builds (the md files are in its compile list) |

**Shown able to fail.** `--m68ktest`: `--poison 50` 25,622 failures; `--no-exclusions` 31,553; `--no-tolerances` 54,529; an injected ADD overflow-flag bug failed ADD/ADDX (`sr`), two extra
idle clocks in NOP failed 2,500 of 2,500 NOP vectors (`bus`, `cycles`): 21,296 failures in all, reverted. `--md-test`: four injected bugs (CRAM reads without the FIFO bits, the old pad numbering,
open bus never updated, DMA words bypassing the FIFO) failed 7 checks, reverted. The test ROMs failed on their own before the fixes (below).

## Against the oracle and the hardware test ROMs

BlastEm 1.0.0 (GPL) is an oracle only: run as a program, its window captured as a picture, keys sent to it. No source read. Its interface is a window: there is no register dump, so comparisons are
screens, which is why they stop at what a screen shows. Our side has no renderer, so the comparison picture is `--md-run --plane-png`: the two scroll planes' first 40x28 cells, no scroll, no window, no
sprites, no priority.

| ROM | BlastEm | Ours | Notes |
|---|---|---|---|
| Nemesis' VDP port access test (83 tests, hardware-derived expectations) | 83 of 83 | **82 of 83** | page 1 was 0 of 9 at first light, 62 of 77 after the first fix; the remaining one is test 16, FIFO wait states (cycle-exact FIFO acceptance timing) |
| MacDonald `memtest_68k` | screen | same words except the HBLANK status bit and the H counter (a read lands a few pixels differently) | found the open-bus bugs: unmapped reads returned FFFF, the status's top bits were a fixed pattern; one read is oracle-derived (the RESET register's bit 8) |
| MacDonald `itest` (11,529 illegal opcodes) | stops at a TMSS dialog | green (CRAM $0E0); a copy with the first opcode changed to RTS turns red | the harness can fail |
| `bcd-verifier` | 0 errors | 0 errors | |
| `m68k_opcode_sizes` | pattern | runs to its end (no errors) | its output is a VRAM table, not interpreted |
| `vctest`, `window-*`, `SpriteMaskingTestRom`, `cram-flicker`, `Direct-Color-DMA`, Titan Overdrive | pictures | not compared | need the renderer (B2) |

## The five roster games (`C:\BrokenNes-tools\sega\...` ROMs, not in git)

Each runs 1,560 frames headless with Start pressed at frames 480, 840 and 1200: 0 halts, 0 address errors, VINT on, display on, DMA used, Z80 running a sound driver (the YM2612 is silent, its
registers and timers work), ~325-350 frames/s (5.4-5.8x real time). The planes at that moment, next to BlastEm at about the same wall-clock time:

| Game | How far | Plane dump against BlastEm |
|---|---|---|
| Flicky | Round 1 | same stage (platforms, windows, HUD text); BlastEm adds the sprites |
| Altered Beast | stage 1 | same temple, ground and sky; sprites missing. At 6 s (frame 360) ours showed only the backdrop where BlastEm had the title screen; by frame 840 it was in the stage (not investigated: the intro's length differs by the real-time capture's offset) |
| Sonic the Hedgehog | Green Hill Zone | same level; scroll phase and sprites differ |
| Golden Axe | stage 1, the typed intro text | same background and text box; the text is a letter apart (frame phase) |
| Strider | stage 1 (Kazakh) | same domes and skyline; HUD is sprites |

None of this says the games are playable: with no sprites and no scroll the picture cannot be judged, no audio is heard, no input beyond Start was tried.

## Decisions

- ares as the 68000 source (ISC, bus-cycle accurate); MAME's vectors win where the two vector sets disagree (silicon-derived).
- The VDP runs on the master clock with per-line slot tables as data (210/171 slots, refresh 5/4, free 18/16 active and 205/167 blanked), spread evenly: the seam for a slot-by-slot VDP.
- The FIFO is a four-entry ring whose stale contents are visible (hardware test result); DMA words and writes to a non-write target pass through it.
- A fill or copy does not hold the 68000 (a bus DMA does); the code register is live during a fill.
- Open bus is the data of the 68000's last bus cycle.
- 6-button pad phases counted in falling edges of TH (see `MdPads`).
- TMSS is accepted and ignored; model 1 and 2 TAS does not write back.

## Open problems and risks

- FIFO wait-state timing (test 16) and everything cycle-exact in the VDP: the slot table is not the hardware's irregular one; the H counter and HBLANK flag are approximations (a read can differ by a few pixels).
- The 68000's interrupt-acknowledge timing and the exact stacked PC of an address error are not covered by any test.
- Z80 bank-window stall figures (3.3 T-states and 9.7 clocks) and the DRAM refresh model are the plan's choices; MacDonald/Overdrive ROMs will tell.
- Open-bus values: right for reads right after a fetch (what the memory test measures); not verified for reads in the middle of multi-word instructions.
- The oracle comparison is by picture only; a register-level comparison needs BlastEm's debugger driven over stdin or its GDB stub.
- Genesis Plus GX source files (`gpgx_core_*.c`, `gpgx_x_*.c`; non-commercial licence) are in the lead's session scratchpad folder (created 14:56-15:01 on 2026-10-03, before this track started; this track neither created nor opened them). They are not to be read for code and should be deleted.

## Next (B2)

1. The renderer: planes with scroll and priority, window, sprites with the per-line limits, H32/H40, shadow/highlight, interlace; then the `--plane-png` dump retires.
2. The real YM2612 (ymfm or Nuked as oracle) and the DAC; Z80 sound drivers (Altered Beast speech) and the bridges.
3. VDP accuracy: test 16, the slot-by-slot VDP, HV counter exactness, sprite masking, window distortion, CRAM dots, Direct Color DMA, Titan Overdrive 1, then Overdrive 2 on PAL.
4. Mappers (SSF2, S&K lock-on, SRAM games), saves under `MdSaves\`, then the 20 roster games and the Genesis-ready flip.

## Reproduce

```
dotnet build Workshop\BrokenNes.Workshop.csproj -c Release --artifacts-path %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_sega_b
Workshop.exe --m68ktest [--selftest | --bench | --jobs 4 | --poison 50 | --no-exclusions | --no-tolerances]
Workshop.exe --md-test
Workshop.exe --md-run <rom> --frames 1560 --input "480:Start,486:" --plane-png out.png [--trace t.txt --trace-pc --trace-reads] [--ram-out ram.bin] [--force]
```

The Workshop exe is a WinExe: start it with `Start-Process` and redirect its output.
