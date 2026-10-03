# Sega family (Master System, Game Gear, Genesis / Mega Drive): provenance ledger

Started 2026-10-03 with the foundation phase. Graded like the SFC ledger: what was taken from source files, what was written from memory, which facts are
public hardware documentation, and what was deliberately not taken. **Update this file in the same change that adds or ports anything**; a core is only
certified once its entries here are complete. The policy and the root list are in `/THIRD_PARTY_NOTICES.md`.

Sourcing plan (decided 2026-10-03, `docs/projects/sega-cores-plan.md`): **hybrid per chip**. Commodity chips are ported from permissive sources with notices;
both VDPs and all bus and cycle timing are written clean-room from documentation and black-box oracle comparisons.

## 1. Taken from source files (direct derivation)

| What | Where | Source | License | Status |
|---|---|---|---|---|
| Z80 core (cycle-stepped micro-op design) | `sega/Z80*.cs` | BizHawk `Z80A` (`BizHawk.Emulation.Cores/CPUs/Z80A`, Alyosha and the BizHawk team), from the local tree `Bizhawk52X-Vanguard` | MIT, Copyright (c) BizHawk team | **planned, not yet ported** |
| YM2612 / YM2413 FM cores | `sega/` | ymfm (Aaron Giles); YM2413 alternatively emu2413 (Mitsutaka Okazaki) | BSD-3-Clause / MIT | planned (tracks) |
| 68000 core | `md/` | ares `m68000` (ISC), Musashi (MIT) as fallback; chosen by a one-day spike | ISC / MIT | planned (Genesis track) |
| SMS mappers and header handling | `sms/` | BizHawk Master System memory-map code | MIT | planned (SMS track) |

(Entries move from "planned" to a dated description of exactly what was taken the day the code lands.)

## 2. Written with a source seen (credited)

| What | Where | Notes |
|---|---|---|
| SN76489 PSG | `sega/Sn76489*.cs` | **planned.** Written from the SMS Power SN76489 documentation (Maxim's notes: latch/data protocol, 16-bit Sega LFSR with taps 0 and 3 reset on a noise write, tone period 0/1 acting as period 1, 2 dB attenuation steps). BizHawk's `SN76489sms.cs` (MIT, 258 lines) was **read on 2026-10-03** before writing, as a cross-check; it is a deliberately simple implementation (it toggles at the PSG clock for periods 0 and 1 instead of holding DC, no band-limiting), so the structure is ours and the behaviour follows the documentation, not it. |

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
  Nuked-OPLL (GPL-2.0), Nuked-MD (GPL-2.0), MDSound (GPL-3.0), clownmdemu and clown68000 (AGPL-3.0). They are oracles: run, compared, never read for code.
- **BizHawk's `YM2413.cs`**: its own header says "uncertain licensing terms" (a port of Okazaki's emulator through a third party). Not used.
- **MAME's `315_5313.cpp`** (Genesis VDP): its own TODO says video, DMA timing and HV counter are incorrect; not a source for the VDP.
- **BIOS and boot ROMs** (Master System, Game Gear, Genesis TMSS): never bundled, never required. Boot starts from the documented post-boot state.
- **Game ROMs**: never committed. Unlicensed test ROMs (Nemesis, MacDonald, FluBBa, sverx, Titan) and GPL test programs (zexall, BlastEm's tests) stay local and out of git.
- **Oracle output** (traces, frame dumps, golden audio rendered through Nuked cores) is data used for comparison; the oracle programs and their scripts are not committed.
