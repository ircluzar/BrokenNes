# Track A4: Master System, Game Gear, SG-1000, round 4 (2026-10-15 on)

Record of the fourth round on the Z80 consoles. Earlier records: `sega-track-a.md`, `-a2.md`, `-a3.md`. Provenance of code and outside facts: `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` (nothing was ported this round; the oracle, Mesen 2.1.1, was only run). Branch `worktree-agent-a314e5c62d8920ea5`, based on `main` = `5c974b2`. The shared bar is in `sega-tracks-brief.md`, "Round 4 additions".

**Status while this is being written:** work in progress; sections say what was run. Anything not listed under a run is not tested.

## What exists (new this round)

| Area | What | Where |
|---|---|---|
| Hot swapping | `IConsoleSession.TrySwapCore` of the Master System / Game Gear takes only ids the console's menus offer (it accepted any CPU id, and a Game Gear took `SMS-FM`); suite `swap` swaps every id of every slot mid-game | `sms/SmsSession.cs`, `Workshop/Sega/SwapSuite.cs` |
| Save states on the roster | suite `rosterstate`: all 40 roster games, F7 / F5 path, round trip and sound-unit refusals | `Workshop/Sega/RosterSuite.cs` |
| UAT | case kind `sega-run` (`--console-run` through the apps' session factory) and 17 cases in `UAT/cases/sega.json` | `UAT/lib/SegaCases.ps1`, `UAT/run-suite.ps1` |
| Oracle tooling | the oracle's PSG / YM2413 registers at every frame end (`capture.lua -Psg`), `--sms-run --sound-regs / --fm-regs-file`, columns in `mesen-compare.ps1`; the 40 roster play scripts; fix for long-named games that "could not be captured" | `Workshop/Sega/mesen/`, `SmsRunCli.cs` |
| Console run | `--console-run` takes `.7z` ROMs, passes the file name (a `(Europe)` dump is PAL as in the app), prints colours and audio peak, `--battery-out` | `Workshop/ConsoleRunCli.cs` |
| Sweep | `--sms-sweep` crashed on every game since the picture wrapper was merged (a cast to `SmsSession`); fixed | `Workshop/Sega/SmsSweepCli.cs` |
