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
| 68000 core | `md/` | ares `m68000` (ISC), Musashi (MIT) as fallback; chosen by a one-day spike | ISC / MIT | planned (Genesis track) |
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

## 2. Written with a source seen (credited)

| What | Where | Notes |
|---|---|---|
| SN76489 PSG | `sega/Sn76489.cs`, `sega/BandLimitedMixer.cs` | **done (2026-10-03).** Written from the SMS Power SN76489 documentation (Maxim's notes: latch/data protocol, 16-bit Sega LFSR with taps 0 and 3 reset on a noise write, tone period 0/1 acting as period 1, 2 dB attenuation steps). BizHawk's `SN76489sms.cs` (MIT, 258 lines) was **read on 2026-10-03** before writing, as a cross-check; it is a deliberately simple implementation (it toggles at the PSG clock for periods 0 and 1 instead of holding DC, no band-limiting), so the structure is ours and the behaviour follows the documentation, not it. Three variants: Master System / Game Gear (period 0 and 1 hold the output high, as documented), Genesis (period 0 is period 1 and the output flips every PSG clock, which is what the oracle shows) and the discrete TI part (15-bit noise). **Verified against Nuked-PSG (GPL-2.0+, die-derived), oracle only:** `Workshop/Sega/oracle/psg_oracle.c` is our own black-box driver (it calls the oracle's public functions and reads two state fields; none of its code is copied) built by `build-oracles.ps1` into the tools folder; `--sega-oracle-test` compares raw channel levels sample for sample: tone periods 0 to 1023 on all three tones and the noise register in all 12 modes over 115,600 samples each, 17 of 17 identical. The oracle's attenuation levels (measured from a Genesis output stage) part from the documented 2 dB steps by up to 12.9 dB at volume 14; the chip keeps the documented steps and the difference is reported by the test, for the Genesis track to decide. |

## 3. Hardware facts and documentation used (public; any correct emulator shares them)

| Used for | Sources |
|---|---|
| ROM detection: headers, region nibbles, SMD interleave | SMS Power "ROM header" and "Mappers" pages; Sega Genesis Technical Overview; Plutiedev "ROM header" and "Beyond 4 MB"; the SMD format as documented on SpritesMind and in emulator docs |
| Timing arithmetic (`SegaTiming`) | Sega Genesis Technical Overview 1.00; Charles MacDonald `gen-hw.txt`; Kabuto's hardware notes; MacDonald's SMS VDP document; SMS Power wiki |
| Console memory maps, ports, interrupts | the same; Sean Young, *The Undocumented Z80 Documented* (for the Z80) |
| Frame sizes (SMS 256x192, Game Gear 160x144 window, Genesis 256/320 x 224/240) | MacDonald's VDP document; Sega Game Gear Hardware Reference Manual; Genesis Technical Overview |

## 4. Deliberately NOT taken

- **Copyleft and non-commercial sources are never copied** and their source is not read for the code we write: Mesen 2 (GPL-3.0), BlastEm (GPL-3.0), Genesis Plus GX
  (non-commercial, and its Z80 is the Buchmueller V3.9 "freeware for non-commercial purposes"), PicoDrive, jgenesis (GPL-3.0), Gearsystem (GPL-3.0), Nuked-OPN2 (LGPL-2.1),
  Nuked-OPLL (GPL-2.0), Nuked-PSG (GPL-2.0+), Nuked-MD (GPL-2.0), MDSound (GPL-3.0), clownmdemu and clown68000 (AGPL-3.0). They are oracles: run, compared, never read for code.
- **BizHawk's `YM2413.cs`**: its own header says "uncertain licensing terms" (a port of Okazaki's emulator through a third party). Not used.
- **MAME's `315_5313.cpp`** (Genesis VDP): its own TODO says video, DMA timing and HV counter are incorrect; not a source for the VDP.
- **BIOS and boot ROMs** (Master System, Game Gear, Genesis TMSS): never bundled, never required. Boot starts from the documented post-boot state.
- **Game ROMs**: never committed. Unlicensed test ROMs (Nemesis, MacDonald, FluBBa, sverx, Titan) and GPL test programs (zexall, BlastEm's tests) stay local and out of git.
- **Oracle output** (traces, frame dumps, golden audio rendered through Nuked cores) is data used for comparison; the oracle programs and their scripts are not committed.
