# Sega family (Master System, Game Gear, Genesis / Mega Drive): provenance ledger

Started 2026-10-03 with the foundation phase. Graded like the SFC ledger: what was taken from source files, what was written from memory, which facts are
public hardware documentation, and what was deliberately not taken. **Update this file in the same change that adds or ports anything**; a core is only
certified once its entries here are complete. The policy and the root list are in `/THIRD_PARTY_NOTICES.md`.

Sourcing plan (decided 2026-10-03, `docs/projects/sega-cores-plan.md`): **hybrid per chip**. Commodity chips are ported from permissive sources with notices;
both VDPs and all bus and cycle timing are written clean-room from documentation and black-box oracle comparisons.

## 1. Taken from source files (direct derivation)

| What | Where | Source | License | Status |
|---|---|---|---|---|
| Z80 core (cycle-stepped micro-op design) | `sega/Z80/*.cs`: `Z80<TBus>`, `IZ80Bus`; verifier `Workshop/Sega/Z80TestCli.cs` | BizHawk `Z80A` (`BizHawk.Emulation.Cores/CPUs/Z80A`, Alyosha and the BizHawk team), from the local tree `Bizhawk52X-Vanguard` (HEAD f9c0100, 2025-02-24) | MIT, Copyright (c) BizHawk team | **ported 2026-10-03**; details in section 1a |
| YM2612 / YM2413 FM cores | `sega/` | ymfm (Aaron Giles); YM2413 alternatively emu2413 (Mitsutaka Okazaki) | BSD-3-Clause / MIT | planned (tracks) |
| 68000 core (`M68k<TBus>`, `IM68kBus`) | `md/M68k*.cs`, `md/IM68kBus.cs`; verifier `Workshop/Sega/M68kTestCli.cs`, `M68kVectors.cs`, `M68kSelfTest.cs`, `M68kBench.cs` | ares `component/processor/m68000` (ares team, Near et al; pin 4cb8d92, local tree `C:\BrokenNes-tools\sega\port-sources\ares-sparse`) | ISC, Copyright (c) 2004-2025 ares team, Near et al | **ported 2026-10-03**; details in section 1b |
| SMS mappers and header handling | `sms/` | BizHawk Master System memory-map code | MIT | planned (SMS track) |

(Entries move from "planned" to a dated description of exactly what was taken the day the code lands.)

### 1a. Z80 (`sega/Z80/`), ported 2026-10-03

**Source read.** BizHawk's `Z80A.cs`, `Execute.cs`, `Operations.cs`, `Registers.cs`, `Interrupts.cs`, `Tables_Direct.cs`, `Tables_Indirect.cs`, `IZ80ALink.cs` and its `ReadMe.txt`, in full. `NewDisassembler.cs` was neither read nor ported.
No other emulator's Z80 source (ares, MAME, Fuse, Genesis Plus GX, ...) was read for this work.

**Taken from BizHawk (MIT, Copyright (c) BizHawk team; the license text is in the root ledger, section 6).**
- The **design**: a generic class over a struct "link", each instruction a short program of micro-operations with one slot per T-state, built when the opcode is fetched; `Tick` is its `ExecuteOne`.
  Memory cycles are `IDLE, WAIT, RD/WR`, I/O cycles have the extra wait state, the M1 fetch is `IDLE, WAIT, OP_F` with the instruction's own program starting in T4.
  The micro-op set (names and numbering kept: `RD_INC`, `WR_DEC`, `TR16`, `ADDS`, `REP_OP_I`, `SET_FL_LD_R`, ...) and the register-index model (`PCl`, `PCh`, ..., `W`, `Z`, `ALU`, shadow set) are BizHawk's.
- The **decode tables** (`FetchInstruction`: six tables, 256 opcode-to-builder entries each except ED, which has 81 and a NOP default; including the undocumented IXH/IXL/IYH/IYL forms, SLL, the DD CB / FD CB register-copy forms and the ED duplicates) and the
  **program builders** (`NOP_`, `LD_IND_16`, `I_OP_n`, `LD_OP_R`, ... 66 of them) were **transcribed with a script**, not retyped: register names prefixed (`rA`), the per-slot bus-request and
  memory-request annotation arrays (`PopulateBUSRQ`/`PopulateMEMRQ`, which only served ZX Spectrum contention) dropped, the six prefix booleans replaced by one state value. The rest of each table and program is BizHawk's text.
- The NMI, IM 1 and IM 2 response programs (`Interrupts.cs`), with the bus columns dropped.
- The names, signatures and slot-level behaviour of the micro-op executors in `Operations.cs` (`Read_Func`, `Write_INC_Func`, `TR16_Func`, ...).

**Changed or rewritten here.**
- `IZ80ALink` (`FetchMemory`, `ReadMemory`, `WriteMemory`, `ReadHardware`, `WriteHardware`, `FetchDB` plus four callbacks) became `IZ80Bus` (`FetchOpcode`, `Read`, `Write`, `In`, `Out`, `InterruptVector`, no callbacks).
  BizHawk's `Serializer` sync, `RegisterValue` register dumps, `IDictionary` flag dumps, trace callback and disassembler were not taken; `SaveState`/`LoadState` over `BinaryWriter`/`BinaryReader` and plain register properties replace them.
- **ALU and flag code** (all `*_Func` bodies) rewritten: registers are bytes, F is composed and written once per instruction, parity comes from a table. The rewrite is what makes the Q latch (below) and the exact flag rules possible.
- Corrections found by the test vectors (BizHawk's own `ReadMe.txt` says its per-cycle tables are "NOT confirmed accurate"):
  `ADD/ADC/SUB/SBC/AND/XOR/OR/CP A,(HL)` no longer change WZ (BizHawk loaded WZ from HL); `RETI` copies IFF2 to IFF1 like `RETN` (BizHawk did not for `ED 4D`);
  `OUTI/OUTD/OTIR/OTDR` decrement B before the port write (BizHawk wrote with the old B; with that order all 4,000 vectors of the four instructions fail on the port address); in `DD CB d op` / `FD CB d op` the operation byte is read one T-state earlier and as a plain read, not an opcode fetch;
  (by reading its code, not by running it) `HALT` incremented R in its own slot as well as at the fetch and a halted CPU repeated a 1-T-state slot that counted a refresh every T-state, so a halted CPU now runs
  4-T-state NOP cycles that each count one refresh and HALT itself counts one; `EI` set IFF1 and IFF2 only after the next instruction, so it now sets them at once and holds the interrupt off for one instruction (the vectors' model, and what `EI; LD A,I` shows).
- Reset state: AF and SP $FFFF, IM 0 (BizHawk: all zero, IM 1).

**Added here (not in BizHawk).**
- The **Q latch** (SCF and CCF take flag bits 5 and 3 from `(Q xor F) or A`; a DD or FD prefix clears Q; POP AF and EX AF,AF' leave Q at 0, as the vectors require) and the **P latch** (LD A,I / LD A,R, with the NMOS quirk that an interrupt accepted right after them clears P/V; switchable).
- **Block-instruction repeat rules**: when LDIR, LDDR, CPIR, CPDR repeat, flag bits 5 and 3 show the high byte of the instruction's address and WZ = address + 1; when INIR, INDR, OTIR, OTDR repeat, H and P/V are recomputed from the new B and the data byte as well.
  Written from the published description of this behaviour (the research that the SingleStepTests/z80 authors and z80test 1.2 build on), without reading any emulator source, then confirmed by 8,000 vectors and by z80test's hardware-recorded CRCs.
- IM 0 with an `RST n` on the data bus (BizHawk assumed a NOP), `ExecuteInstruction`, `AtInstructionBoundary`, `Reset`/`ResetPin`, `RaiseNmi` (edge) and `Irq` (level), the WAIT pin (`Wait`, BizHawk's `FlagW`), a cap on prefix runs so a board loop cannot be trapped in memory full of $DD, and the program buffer filled
  through `params ReadOnlySpan<ushort>` instead of a 38-argument call (about 60 percent faster).
- The verifier (`Workshop/Sega/Z80TestCli.cs`): written here.

**Verification (2026-10-03; `--z80test`).**
- SingleStepTests/z80 v1.0-beta.2 (commit ebe1875, MIT): **all 1,604 files, 1,604,000 tests pass**, with no file or field excluded: every register, the shadow set, IX/IY, I, R, WZ, Q, P, EI, IFF1/2, IM, RAM, the T-state count,
  every memory and I/O access (kind, address, data and the T-state it happens at) and the port list. The harness was shown to fail (bugs injected on purpose: wrong half-carry, SCF ignoring Q, one T-state too few for INC rr, ADC HL half-carry) with annotated failures, then reverted.
- Patrik Rak's z80test 1.2a (MIT; checks against CRCs recorded on a real Zilog Z80): z80full, z80doc, z80flags, z80docflags, z80ccf and z80memptr all pass; the NEC and ST variants of the SCF/CCF tests are skipped by the program itself.
- zexdoc and zexall (GPL-2.0, Cringle; local only, run as test data): all 67 tests of each report OK (46.7 billion T-states each).
- 57 hand-written checks of what the vectors cannot reach (interrupts of every mode, NMI, HALT, EI delay, WAIT, save states, the I/O address bus, documented instruction timings).
- Not covered by any test: the exact WZ after an interrupt response, IM 0 with anything but RST, bus requests, and the address and data bus levels during T-states with no access (the core does not expose them).

### 1b. 68000 (`md/M68k*.cs`), ported 2026-10-03

**Source read.** ares' `component/processor/m68000`: `m68000.hpp`, `m68000.cpp`, `memory.cpp`, `registers.cpp`, `traits.cpp`, `conditions.cpp`, `effective-address.cpp`, `algorithms.cpp`, `instructions.cpp` and `instruction.cpp`, in full, at the pinned commit above. `disassembler.cpp` and `serialization.cpp` were
neither read nor ported. Musashi (MIT, the planned fallback) was not read: the spike (reading ares for how it carries bus access times) decided for ares, which writes every instruction as the chip does it (internal cycles, then four-clock word-wide bus cycles, with the prefetch
a real bus read where the chip puts it), which is what a board that must timestamp each access needs. No other emulator's 68000 source (MAME, Musashi, Moira, Genesis Plus GX, jgenesis, BlastEm, ...) was read for this work. The MAME-generated test vectors
(`singlesteptests-m68000`, MIT) are data, not source.

**Taken from ares (ISC, Copyright (c) 2004-2025 ares team, Near et al; the license text is in the root ledger, section 6).**
- The **instruction set as code**: every instruction (`instructionADD`, `instructionMOVEM_TO_MEM`, `instructionDIVS`, ...) with the order of its bus cycles and the number of internal clocks between them, the effective-address fetch / read / write routines with their `Hold` and `Fast` variants,
  the flag algorithms (`ADD`, `SUB`, `CMP`, the eight shifts and rotates, BCD), the condition table, the exception skeleton, the TAS lock hook.
- The **set of legal opcodes and their addressing-mode restrictions** (the loops of `instruction.cpp`).
- The **model of time**: a bus access is `wait(4)` plus one word-wide access (`upper`, `lower` strobes), an internal period is `idle(n)`.

**Changed or rewritten here.**
- The bus: ares' `read(upper, lower, address)` / `write(...)` virtual calls and its `idle` / `wait` became `IM68kBus` (a struct-generic bus like `IZ80Bus`): every call carries the clocks elapsed since the previous call, so a board can run the VDP, Z80 and refresh up to the access; interrupts are a polled level plus an acknowledge cycle.
- The decode: ares' 65,536 `std::function` closures bound to operand objects became one byte per opcode (a handler number) with the operand fields decoded when the instruction runs; operand sizes are runtime values, not templates.
- Corrections found by the SingleStepTests vectors (ares' own timing is close but not the vectors'): the stack pushes of a long (LINK, BSR, JSR, PEA push the high word first, unlike the -(An) writes of MOVE and MOVEM, which go low word first); read-modify-write long writes go low word first; ADDX/SUBX -(An) read a long low word first; the order and the idle cycles of the exception frame
  (PC low, SR, PC high; four idle clocks for TRAP, privilege, illegal; none after the real prefetch of TRAPV; the two clocks before the last prefetch for every exception, not only interrupts); the dummy read of ANDI/ORI/EORI to CCR and SR and of MOVE to CCR and SR; MOVE to an absolute-long destination takes its second address word
  from the program stream after (memory source) or before (register or immediate source) the write; BTST with an immediate operand; the early-overflow and flag rules of DIVS/DIVU, CHK and STOP follow the MAME vectors (silicon derived), see below.
- Reset state: SR $2700, registers zero (ares: the same).

**Added here (not in ares).**
- The **address-error exception** (ares had none): the faulting access is aborted after its four clocks, the 14-byte frame (PC low, SR, PC high, the instruction word, the faulting address, the status word with read/write and function code) is stacked in the chip's order, a double fault (address error while stacking) halts the processor. Detected for word and long accesses and for program fetches. 50 clocks.
- The **trace exception** (the T bit), **STOP** (nothing fetched until an interrupt; the stacked return address is the instruction after it), the **Halted** state, the TAS switch (`TasWritesBack`), the RESET instruction's reset pulse.
- The verifier: `Workshop/Sega/M68kTestCli.cs`, `M68kVectors.cs`, `M68kSelfTest.cs`, `M68kBench.cs`, written here.

**Behaviours the two vector sets disagree on, and which the core follows.** SingleStepTests/680x0 (a reference implementation written to the manual, no licence stated, local only) and SingleStepTests/m68000 (generated from MAME's microcoded core, which is derived from the chip, MIT) contradict each other on
the following; the core follows the MAME set, which it passes in full, and the 680x0 vectors in these classes are excluded from the verifier (counted, run anyway, and reported: see the verifier's output):
RTE and RTR read the status word first (680x0: the PC's high word first); LINK A7 pushes the original A7 (680x0: decremented); CHK (where its prefetch falls, the branch conditions, the flags); DIVS/DIVU overflow (DIVS runs its full length unless the quotient is 65,536 or more, N is set and Z cleared; 680x0: early exit from 32,768, N and Z untouched);
ADDQ.L / SUBQ.L to An take 8 clocks (680x0: 6); ASR by a count above the width of a negative value leaves C and X set (680x0: clear); one DIVU by zero (680x0 stacks the instruction's own address); two ASL.B vectors with an impossible result.
**Tolerances** (one field of a vector is not compared): the stacked PC of an address-error frame in the MAME vectors (it follows microcode state; the core stacks instruction address plus the words consumed, which is what the 680x0 vectors show and which they pass), the address registers' values after an aborted access
(the sets and the core differ on whether the post-increment or pre-decrement has happened), the instruction word in the 680x0 vectors' frame and the function code of PC-relative operands, the flags in MAME's MOVE.L address errors, the DBcc counter in the 680x0 address errors, MAME's eight extra idle clocks after the aborted access, and the I/N bit of the 680x0 status word.

**Verification (2026-10-03; `--m68ktest`).**
- SingleStepTests/680x0 (124 files, 1,000,060 vectors): 967,285 pass, 32,775 are excluded by the classes above, **0 fail**. SingleStepTests/m68000 (127 files, 317,500 vectors): **all 317,500 pass**, none excluded, tolerances as above.
  Every register, SR, the prefetch queue, every written byte, the clock count and every bus transaction (kind, clocks, address, size, data, in order) are compared. The harness was shown to fail (re-run on 2026-10-03 for the record): `--poison 50` (every 50th expected value corrupted) 25,622 failures; `--no-exclusions` 31,553 (the excluded 680x0 classes do disagree with the core, they are not hidden passes); `--no-tolerances` 54,529;
  a bug injected into the core (ADD's overflow flag forced to 0) failed ADD, ADDX and their relatives with `sr` mismatches (437 of the 2,500 ADD.b vectors alone), and two extra idle clocks in NOP failed 2,500 of 2,500 NOP vectors with `bus` and `cycles` mismatches (21,296 failures in all); both were reverted.
- 44 hand-written checks (`--m68ktest --selftest`): reset, interrupt mask and level 7, the acknowledge, STOP and waking, privilege violation, the exception vectors, address-error frames, the double fault, TAS on the Genesis, the trace exception, byte lanes, RESET, stack switching, bus accounting (every counted clock reaches the bus) and all 65,536 opcodes run without a host exception.
- Throughput (`--m68ktest --bench`): 420 to 770 million 68000 clocks per second on the three kernels (55 to 100 times the Genesis's real speed; 75.7 times on average) on this machine, the core alone.
- Not covered by any test: interrupt acknowledge timing (the 54-clock autovector figure is ares', an average of the synchronised acknowledge), the exact stacked PC of an address error, instruction-fetch bus errors (the Genesis has none), the bus error exception (no source raises it here), MOVEP and MOVEM to odd addresses beyond the vectors.

## 2. Written with a source seen (credited)

| What | Where | Notes |
|---|---|---|
| SN76489 PSG | `sega/Sn76489.cs`, `sega/BandLimitedMixer.cs` | **done (2026-10-03).** Written from the SMS Power SN76489 documentation (Maxim's notes: latch/data protocol, 16-bit Sega LFSR with taps 0 and 3 reset on a noise write, tone period 0/1 acting as period 1, 2 dB attenuation steps). BizHawk's `SN76489sms.cs` (MIT, 258 lines) was **read on 2026-10-03** before writing, as a cross-check; it is a deliberately simple implementation (it toggles at the PSG clock for periods 0 and 1 instead of holding DC, no band-limiting), so the structure is ours and the behaviour follows the documentation, not it. Three variants: Master System / Game Gear (period 0 and 1 hold the output high, as documented), Genesis (period 0 is period 1 and the output flips every PSG clock, which is what the oracle shows) and the discrete TI part (15-bit noise). **Verified against Nuked-PSG (GPL-2.0+, die-derived), oracle only:** `Workshop/Sega/oracle/psg_oracle.c` is our own black-box driver (it calls the oracle's public functions and reads two state fields; none of its code is copied) built by `build-oracles.ps1` into the tools folder; `--sega-oracle-test` compares raw channel levels sample for sample: tone periods 0 to 1023 on all three tones and the noise register in all 12 modes over 115,600 samples each, 17 of 17 identical. The oracle's attenuation levels (measured from a Genesis output stage) part from the documented 2 dB steps by up to 12.9 dB at volume 14; the chip keeps the documented steps and the difference is reported by the test, for the Genesis track to decide. |

### 2a. Genesis board, VDP, pads, cartridge and sound unit (`md/BOARD_MD.cs`, `MdVdp.cs`, `MdPads.cs`, `MdCartridge.cs`, `MdSoundUnit.cs`, `MdSession.cs`), written 2026-10-03

**No emulator source was read for any of these.** They are written from the documents in section 3 and checked against test ROMs and a black-box oracle:

- **Test ROM sources seen (local only, no licence stated, not committed):** Nemesis' VDP port access test ROM (`VDPFIFOTesting`, 83 tests, assembly source with its expected results and the author's notes on what the hardware does) and Charles MacDonald's `memtest_68k`, `itest` (illegal instruction test) and `vctest`, and realmonster's `m68k_opcode_sizes` (MIT). Their *expected data and comments* are the hardware facts used: the FIFO is a ring of four entries whose stale data is what the unused bits of CRAM, VSRAM and 8-bit reads show; DMA words, writes to a read target and the word that starts a fill pass through it; a register write puts 10 in CD1:0; CRAM and VSRAM fills repeat the next FIFO entry's data while VRAM fills repeat a high byte; a fill moves the source registers and a copy moves bytes at address^1; VSRAM has 40 words; HV counter latch; the way an odd VRAM address swaps bytes. The tests' own code was not copied into anything: `MdBoardTest.cs` holds the expected values it needs, cited by test number.
- **BlastEm 1.0.0 (GPL-3.0) as an oracle only:** run as a program; its screen was captured as a picture (window screenshots, key presses sent to the window). Its source was never read and none of its files were opened by us. Comparing its screen with ours on MacDonald's memory-test ROM found the open-bus bugs (unmapped reads, the VDP status's top bits, the BUSREQ and RESET registers' other bits); running the VDP port test ROM on both found the FIFO ones. Where it shows something the hardware tests do not (the RESET register reads back its bit 8) the code says "oracle-derived".
- **The 6-button pad** follows the phase sequence of Sega's pad documentation (eight TH phases, counted in falling edges), checked by `--md-test`.

**Verification (`--md-test`, 113 checks; `--md-run`).** See `docs/projects/sega-track-b.md`: Nemesis' port test 82 of 83 (BlastEm 83 of 83), MacDonald's illegal-instruction test green, BCD verifier clean, five roster games running into gameplay. Shown able to fail by injecting bugs (FIFO unused bits, pad phase numbering, open bus, DMA bypassing the FIFO), and by the test ROMs themselves (page 1 of the VDP port test was 0 of 9 before the first fix, 62 of 77 after it, 82 of 83 now; a patched `itest` turns red).

## 3. Hardware facts and documentation used (public; any correct emulator shares them)

| Used for | Sources |
|---|---|
| ROM detection: headers, region nibbles, SMD interleave | SMS Power "ROM header" and "Mappers" pages; Sega Genesis Technical Overview; Plutiedev "ROM header" and "Beyond 4 MB"; the SMD format as documented on SpritesMind and in emulator docs |
| Timing arithmetic (`SegaTiming`) | Sega Genesis Technical Overview 1.00; Charles MacDonald `gen-hw.txt`; Kabuto's hardware notes; MacDonald's SMS VDP document; SMS Power wiki |
| Console memory maps, ports, interrupts | the same; Sean Young, *The Undocumented Z80 Documented* (for the Z80) |
| Frame sizes (SMS 256x192, Game Gear 160x144 window, Genesis 256/320 x 224/240) | MacDonald's VDP document; Sega Game Gear Hardware Reference Manual; Genesis Technical Overview |
| Genesis VDP: registers, command/data protocol, FIFO, DMA, counters, interrupt timing, status bits | Sega Genesis Technical Overview; MacDonald's `genvdp.txt` and `gen-hw.txt`; Kabuto's VDP notes; Nemesis' test ROM results (above) |
| Genesis I/O: version register, 3- and 6-button pads (TH counting, timeout), BUSREQ/RESET, bank register, mapper at $A130xx, SRAM header | Sega Genesis Technical Overview; Sega Retro's I/O and mapper pages; Plutiedev (SRAM, "Beyond 4 MB"); MacDonald's `gen-hw.txt` |

## 4. Deliberately NOT taken

- **Copyleft and non-commercial sources are never copied** and their source is not read for the code we write: Mesen 2 (GPL-3.0), BlastEm (GPL-3.0), Genesis Plus GX
  (non-commercial, and its Z80 is the Buchmueller V3.9 "freeware for non-commercial purposes"), PicoDrive, jgenesis (GPL-3.0), Gearsystem (GPL-3.0), Nuked-OPN2 (LGPL-2.1),
  Nuked-OPLL (GPL-2.0), Nuked-PSG (GPL-2.0+), Nuked-MD (GPL-2.0), MDSound (GPL-3.0), clownmdemu and clown68000 (AGPL-3.0). They are oracles: run, compared, never read for code.
- **BizHawk's `YM2413.cs`**: its own header says "uncertain licensing terms" (a port of Okazaki's emulator through a third party). Not used.
- **MAME's `315_5313.cpp`** (Genesis VDP): its own TODO says video, DMA timing and HV counter are incorrect; not a source for the VDP.
- **BIOS and boot ROMs** (Master System, Game Gear, Genesis TMSS): never bundled, never required. Boot starts from the documented post-boot state.
- **Game ROMs**: never committed. Unlicensed test ROMs (Nemesis, MacDonald, FluBBa, sverx, Titan) and GPL test programs (zexall, BlastEm's tests) stay local and out of git.
- **Oracle output** (traces, frame dumps, golden audio rendered through Nuked cores) is data used for comparison; the oracle programs and their scripts are not committed.
