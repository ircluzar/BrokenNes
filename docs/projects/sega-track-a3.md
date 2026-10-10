# Track A3: Master System, Game Gear and SG-1000, round 3 (2026-10-09)

Record of the third round on the Z80 consoles: what was built, where it is, what was measured, what was decided and what remains. Earlier records: `sega-track-a.md` (A1), `sega-track-a2.md` (A2). Provenance of the code and of every fact taken from outside:
`Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`, section 1h. Branch `worktree-agent-a25c4b0a9e39d0386`, never merged or pushed by this track.

## What exists now

| Area | What | Where |
|---|---|---|
| Whole-library sweep | `--sms-sweep <folder, list, archive or ROM>`: every game runs headless under a scripted pad through the app's own session, and is classified (crash, hung, no picture, blank, dark, noisy, static, silent, audio, ok) with the evidence that decided it (ROM-space writes, cartridge-RAM writes, ports polled, VDP modes, battery, PSG writes, the last hash). Unpacks `.7z` and `.zip`, contact sheets, `--model` to run every game as another board, `--selftest [--poison]` (six programs, one per class) | `Workshop/Sega/SmsSweepCli.cs`, route in `Workshop/Program.cs` |
| Mesen at scale | `mesen-compare.ps1`: a list of games, each against two oracle runs frame by frame (or at checkpoints), several comparison modes per game, a TSV per run; `menu-scripts.tsv`: input scripts that get past menus; the capture script gained a RAM power-on state, the VDP revision setting, a plain-named ROM copy | `Workshop/Sega/mesen/` |
| Save states for every session | `IConsoleSession.SupportsState / StateUnsupportedReason / SaveState / LoadState` (default members), the container `ConsoleStateFile` (magic, console, game, sound unit, region, format, CRC, deflate), the Master System / Game Gear and Game Boy / Game Boy Color sessions implement it; desktop F7 / F5 and the Save / Load State items; Lite buttons | `Windows/NesEmulator/systems/ConsoleStates.cs`, `sms/SmsSession.cs`, `Windows/MainForm/MainForm.SessionStates.cs`, `board/Emulator.Consoles.cs` |
| 24C01 serial EEPROM | The four Game Gear baseball cartridges that save into one (World Series Baseball V1.0, V1.1 and '95, The Majors Pro Baseball) | `sms/Eeprom24c01.cs`, `SmsMapperKind.SegaEeprom` |
| Cartridge table | By SHA-1: the four EEPROM games, two Korean `$A000` games, and the games that need the original VDP (`KnownModels`) | `sms/SmsCartridge.cs` |
| BIOS images | A file named as a BIOS runs as the console's BIOS from a cold power-on with an empty slot (the user's own images, none shipped) | `sms/SmsSession.cs`, `sms/BOARD_SMS.cs` |
| TMS modes on the SMS and GG chips | The fixed six-bit colours of the chip; the SG-1000's TMS9918A keeps its own | `sms/SmsVdp.Render.cs` |
| Tool-only oracle settings | `OracleHaltNoRefresh` (R does not count while halted, as the oracle's does not), next to A2's `OracleZ80Timing` | `sms/BOARD_SMS.cs`, `sega/Z80/Z80.cs` |
| Suites | `--smstest`: `sessionstate`, `eeprom`, `bios`, `known`, `refresh` are new, `sg1000` and `mapper` grew; every one has a proof of failure | `Workshop/Sega/SessionStateSuite.cs`, `EepromSuite.cs`, `RefreshSuite.cs`, `Sg1000Suite.cs`, `SmsSuitesHw.cs` |

@@SWEEP@@

@@COMPARE@@

## The VDP's VRAM access queue: decision (kept as is, with the evidence)

**Question (A2 left it open).** During the active display the oracle changes the effect of some VRAM data writes (two writes 12 to 63 cycles apart: dropped, swapped or one byte late; about 8 percent of isolated writes in a sweep) while this emulator keeps every write, in order, at its own address. Is that the hardware's access queue, which this emulator should model, or the oracle's artifact?

**What the documents say.** Charles MacDonald's VDP document (the section on VRAM access timing), Maxim's notes on the SMS Power wiki and sverx's 2014 measurements agree that on a real console a data-port write made too soon after the previous access during the active display is lost or lands wrong, with the safe spacing given as 22.8 to 26 cycles (sverx: faster than 26 cycles corrupts, address-register writes are fine).

**What the oracle does, measured here.** `vram-double-on-*.sms` (a pair of data writes at 12 to 65 cycles apart, 144 trials): 64 trials differ from this emulator's result with this round's build (A2 counted 34 with its build); the results are phase-dependent patterns (the second write lost, the pair swapped to `a ^ 1`, the first lost) and they occur at spacings up to 65 cycles, well beyond the documented 26. The same oracle shows **no** corruption in a real game that writes faster than the documented limit: Shinobi (Japan) on the Master System has 190 data-port write gaps of 15 to 16 cycles in the active display (10 of 15 and 180 of 16; `--sms-run --fast-writes 26` prints the histogram) and this emulator, which models no corruption, equals the oracle on 898 of 898 frames of it (a run of this round). So "a gap under 26 cycles corrupts" is contradicted by the oracle itself on the one game that tests it, and the oracle's probe results contradict the documents in the other direction (corruption at 65 cycles). SMSTestSuite 0.38 tests audio and the display, not this, so it cannot break the tie.

**Where the game differences are not from VRAM.** Bubble Bobble (both consoles), the game that started the question: its first difference is frame 113, a sprite X one pixel off on six lines, and later work RAM bytes in the stack area (`$1FF0` to `$1FF4`) and counters one off: the signature of the frame ending at a slightly different instant against the main thread, which points away from lost VRAM writes (a pointer, not a proof).

**Decision.** No access-queue corruption is modelled. Hardware and oracle cannot be told apart without a real console, and either guess would make a measured game worse (Shinobi). Not decided by evidence, so not hidden: the probes (`--write-vdp-probes`, `--vram-double`, `--vram-sweep`) and the gap histogram stay in the tool for whoever has hardware to run them on; the ROMs are small and generated by the Workshop.


@@OPEN@@

@@STATES@@

@@HARDWARE@@

@@VERIFIERS@@

@@DECISIONS@@

@@NEXT@@
