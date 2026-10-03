# Sega tracks: shared brief for the agents (read this first)

Phase 0 is done and on `main` (see section 12 of `sega-cores-plan.md`). Three tracks now build on it, each in its own git worktree and branch, by its own agent:
**A** Master System / Game Gear (and SG-1000), **B** Genesis, **C** bridges and plugin. This page holds what all three share. Your track's mission is in the prompt that started you.

## Read before writing code

1. `docs/projects/sega-cores-plan.md` (whole; sections 3-8 and 12 matter most), `docs/projects/sega-rosters.md`, `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`, root `THIRD_PARTY_NOTICES.md`.
2. Memory notes (plain markdown): `C:\Users\philt\.claude\projects\C--Users-philt-OneDrive-Documents-PROJECTS-BrokenNes\memory\` : `project_sega_cores_plan.md`, `project_selftest_toolset.md` (how tests are driven, the traps),
   `feedback_no_silent_fallbacks.md`, `feedback_always_commit.md`, `project_snes_classic_climb.md` (the climb workflow and the Mesen chip-diff recipe), `project_gb_cores.md`, `project_snes_perf.md`.
3. What exists in `Windows/NesEmulator/sega/`: `Z80/` (real Z80, `Z80<TBus>`, `IZ80Bus`), `Sn76489.cs`, `BandLimitedMixer.cs`, `SegaTimeline.cs` (clocks, both regions), `SegaSettings.cs` (region, models), `SegaBoardParts.cs`,
   `ISmsSound.cs` + `SmsSound`, `IGenesisSound.cs` + `GenesisSoundStub`, `SegaHub.cs` (`HubVoice`, `NesVoiceWriter`), `SegaRomFormat.cs`, `SegaPlaceholderSession.cs`; the console layer in `Windows/NesEmulator/systems/`
   (`ConsoleSystems.cs`: `ConsoleKind`, `Consoles` with the `*Ready` flags and `SegaPreview`, `CoreCatalog`; `ConsoleSessions.cs`: `CreateSega`). Study how the GB (`gb/`) and SNES (`snes/`) families are built (`BOARD_GB`, `BOARD_SFC`,
   their sessions, `IConsoleSession`) and follow their shape.
4. The Workshop verifier CLIs: `Workshop/Sega/*TestCli.cs`, `Z80TestCli.cs` (a good model for a verifier with a proof-of-failure), `Workshop/Gb/`, `SnesTestCli.cs`, `ConsoleRunCli.cs`.

## Hard rules

- **Your own worktree and branch.** Commit there, often, with a message that says what changed and what was verified, ending with the attribution line the harness gives you. **Never push, never merge, never touch `main`.** The lead merges.
- **Builds:** always `dotnet build <csproj> -c Release --artifacts-path %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_sega_<yourtrack>` (a: `bn_sega_a`, b: `bn_sega_b`, c: `bn_sega_c`) so you never clobber another agent. **Check the build exit code before running any exe**:
  a stale Workshop exe falls through to the GUI and hangs. Run exes through `Start-Process ... -RedirectStandardOutput` with a timeout and kill on timeout. This machine has 12 threads shared by three agents plus the user: keep parallel jobs to 4 or fewer
  and never leave long runs unattended without a timeout.
- **Licensing (the repo license is bespoke and cannot carry GPL).** Port only from MIT, BSD, ISC or zlib sources, with a ledger row in `sega/THIRD_PARTY_NOTICES.md` saying what was taken, from where, at what version, and what was changed. GPL, LGPL, AGPL and non-commercial emulators
  (Mesen2, BlastEm, Genesis Plus GX, jgenesis, Nuked-*, Gearsystem, MAME for GPL parts) are **oracles only**: run them, compare to them, never read their code for ideas and never copy from them. Say in the ledger if you read a permissive source.
- **No ROMs and no firmware in git, ever.** Games are in `X:\EMULATION\Sega Master System` (`.7z`, one game per archive), `Sega Game Gear`, `Sega Genesis` (see `sega-rosters.md`). No BIOS or boot ROM may ever be required: use the documented post-boot state.
- **Test assets and oracles** are in `C:\BrokenNes-tools\sega` (outside the repo, hash-verified, `fetch-tools.ps1` re-creates it): `test-sets\` (Z80, 68000 vectors, z80test, zexall, SMS test ROMs, 21 Genesis test ROMs), `oracles\` (Mesen 2.1.1, BlastEm 1.0.0, Nuked sources, a PSG
  harness), `port-sources\` (ares sparse checkout, ymfm, emu2413, Musashi). Mesen2 covers SMS and Game Gear; BlastEm covers Genesis. Drive oracles black-box (command line, Lua, frame dumps); the SNES climb notes explain the Mesen workflow.
- **No silent fallbacks.** If something cannot work it must say so (an error, a labelled stub, a counted drop). Never substitute another core, roll back, or hide a failure so a test passes.
- **Honest verification.** Report only what you ran. Every verifier you write must be shown able to fail (inject a bug or poison an input, see it flagged, revert). Distinguish "passes" from "not tested".
- **Shared files** (`ConsoleSystems.cs`, `ConsoleSessions.cs`, `Workshop/Program.cs`, `sega/THIRD_PARTY_NOTICES.md`, project files, `docs/`): other tracks edit them too. Keep your edits there small, additive and local (one route line, one ledger row, one flag), so the merge is trivial.
  The Phase 0 contract files in `sega/` are shared: if you must change one, make the smallest additive change, test it with `--sega-contracts-test`, and say so in your report.
- **The preview switch.** The consoles stay hidden unless `BROKENNES_SEGA=1` / Lite `?sega=1`. Flip your console's `*Ready` flag only when its exit gate (in your prompt) is met; otherwise leave it behind the switch and say so.
- **Do not break the rest.** NES, SNES and GB behaviour must be unchanged: the golden-hash and smoke suites exist for that (`project_selftest_toolset.md`; `UAT\entrypoints\desktop-smoke.ps1`, `UAT\entrypoints\sega-preview-smoke.ps1`, `--sega-test`, `--sega-contracts-test`, `--z80test --selftest`).
- **The dashboard** (https://claude.ai/artifact/BJeTEhpACkjebnpYtcm6ej) is the user's live view. Update your track's documents as you reach milestones (`phases/<id>` gates, `suites/<id>`, `games/<console>-NN` status and checks, `activity/feed` newest first, `meta/focus`):
  load the `ArtifactData` tool with ToolSearch, read a document before writing it, and pass `if_version`. Several agents write there: re-read on a version conflict and redo your write. Only put verified facts on it.
- **Finish what you start.** Work to your milestone's exit gate, not to a plan. Stop early only for a decision that is the user's (say exactly what you need) or a hard blocker (say what you tried). Your final report is read by the lead, not the user: be specific and complete.

## What to put in your final report

What was built and where (files); every verifier you ran with its numbers; proof that each verifier can fail; what is not tested; decisions you made and why; shared files and contracts you touched; open problems and risks; the exact next steps for the next milestone of your track; the branch name and last commit.
## Seams the tracks share (decided here so nobody collides)

- **Naming.** Classes named `CPU_*`, `PPU_*`, `APU_*` that implement `ICPU`/`IPPU`/`IAPU` are the *NES-facing bridge cores* that `CoreRegistry` discovers (Track C owns `APU_SMS`, `APU_MD`, `PPU_SMS`, `PPU_MD`, `CPU_Z80`, `CPU_M68K`).
  The console-native chips and boards of Tracks A and B must **not** use those prefixes: use `BOARD_SMS` / `BOARD_MD`, and names like `SmsVdp`, `MdVdp`, `M68k`, `Ym2612`, `Ym2413`.
- **Sound units** are looked up by id, never named by a board: `SmsSoundFactories.Create(id, new SmsSoundRequest(machine, console, variant))` returns an `ISmsSound` and `GenesisSoundFactories.Create(id, machine)` an `IGenesisSound`
  (`sega/SegaSoundFactories.cs`; add a unit by adding one line to its `Factories` dictionary). A session asks for the unit the console's APU menu selected (`SelectedCore(console, CoreSlot.Apu)`; ids `SMS` and `MD` are the stock units).
  Track A replaces what `SMS` builds with a unit that also has the YM2413; Track B replaces what `MD` builds with the real Z80 + YM2612 + PSG unit; Track C adds ids such as `NES` (a Sega game's sound replayed on NES chips).
- **Timeline.** Boards run on `SegaTimeline` (`SegaTimeline.MasterSystem(region, console)`, `SegaTimeline.Genesis(region)`) and `SegaSettings` (region, Genesis model); never a hard-coded NTSC number.
- **Console-native vs NES register hub.** Bridges to and from other consoles go through the NES register model (`SegaHub.cs`: `HubVoice`, `NesVoiceWriter`, `IHubVoiceSource`); a board exposes what it is playing through `DescribeVoices`.