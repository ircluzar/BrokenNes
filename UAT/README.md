# BrokenNes UAT harness

A side-project sanity-testing harness for the Windows desktop app and the Web build. Deliberately
kept separate from `Windows/`, `Web/`, `WebLite/`, and `Workshop/` - nothing in those shipped
projects references this folder, and nothing here modifies them. Just PowerShell scripts and
findings.

## What's here

- `run-suite.ps1` + `cases/*.json` - **the regression suite.** The only thing here that returns a
  verdict instead of a report. See "The regression suite" below.
- `lib/ApiClient.ps1` - **the primary way to drive the desktop app.** A PowerShell client for
  BrokenNes.Windows's local HTTP control API. Read this first.
- `lib/UiaHelpers.ps1` - UI Automation helpers, the fallback for what the API cannot reach, with
  the non-obvious quirks of automating the WebView2-hosted screens written up as comments.
- `findings/` - one markdown file per test pass, written by whichever agent/session ran it.
- `screenshots/` - visual evidence referenced from findings files.

---

## Rule zero: never kill BrokenNes by process name

```powershell
# NEVER. This kills every agent's instance on the machine, not just your leftovers.
Get-Process -Name BrokenNes.Windows | Stop-Process -Force

# Do this instead.
Stop-BrokenNesInstance -ProcessId $inst.ProcessId          # yours, by PID, graceful
Assert-BrokenNesNoForeignInstances -MineProcessId $inst.ProcessId   # look, don't kill
```

Several agents run their own `BrokenNes.Windows.exe` here at the same time (past runs have had ten
live at once). A name-wide `Stop-Process` in one agent's clean-slate preamble kills the instance
another agent is mid-test on, and the victim sees a flawless silent death: no dialog, no WerFault,
no event-log entry, `ExitCode -1`. That produced `findings/direct-play-r2.md`'s "rapid keyboard
input silently kills the process" report, which cost several sessions before being traced, on
2026-09-04, to exactly this. See that file's resolution banner.

**Before filing a "the process vanished" bug**, run the app with `BROKENNES_DIAG=1` and read
`%LOCALAPPDATA%\BrokenNes\diagnostics\shutdown-<pid>-*.log`:

| What the log shows | What it means |
|---|---|
| stops mid-stream, no lifecycle lines, exit code -1 | terminated from **outside** the process |
| `WM_CLOSE` → `OnFormClosing` → `ApplicationExit` → `ProcessExit`, exit 0 | normal shutdown |
| `Exit menu item invoked` | something reached `Emulator > Exit` |
| `*** NATIVE EXCEPTION ***` / `UnhandledException` | a real in-process crash |

---

# The regression suite

Every other tool in this repo prints a report and leaves a human to decide whether the numbers are
right. `--romtest` emits a provably deterministic framebuffer hash and then nobody can say whether
that hash is correct. `run-suite.ps1` is the missing half: `cases/*.json` stores the **expected**
value for each run, the runner produces the **actual** value, diffs them, prints a table, and exits
non-zero when they disagree.

## Read this before you trust a green run

**A green suite proves determinism and the absence of unintended change. It does NOT prove
correctness.**

A framebuffer hash at frame N says "these 245,760 bytes are the same bytes as last time". A
consistently wrong renderer produces a perfectly stable hash, run after run, forever. Every golden
in `cases/` means *"this is what this build did on 2026-09-03"* - not *"this is what a real NES
does"*. Nothing in this suite was compared against hardware, against another emulator, or against a
test ROM with a known-correct answer.

So:

- Green = nothing moved. That is genuinely valuable - it is what catches an accidental regression in
  a shared code path, and it is the whole reason the suite exists.
- Green is **not** evidence that a bug is fixed. If you fix a rendering bug, the correct outcome is a
  RED suite plus a human confirming the new pixels are better.
- A case whose ROM never gets past a black title screen has a stable hash that proves almost
  nothing. `distinctColors` in each golden is the honesty check for this: `corpus-m005-mmc5-cv3`
  records 3 colours and is therefore weak evidence; `story-page2-jimmy-fix` records 15 and is
  strong.
- For actual accuracy, use `--accuracycoin` and the AccuracyCoin test ROMs. That tool answers a
  different question and this suite does not replace it.

## Running it

```powershell
# Everything. ~55s for 60 cases on this machine.
.\UAT\run-suite.ps1

# A subset. -Filter is a wildcard matched against case id, suite name and tags.
.\UAT\run-suite.ps1 -Filter 'mapper30*'
.\UAT\run-suite.ps1 -Filter 'corpus-*'
.\UAT\run-suite.ps1 -Filter 'fix'            # tag match: every FIX-family case

# Machine-readable output alongside the table.
.\UAT\run-suite.ps1 -Json .\UAT\findings\suite-2026-09-03.json

# Determinism audit: run each case N times and fail if the N runs disagree with each other.
.\UAT\run-suite.ps1 -Repeat 3
```

Other flags: `-StopOnFirstFailure` (abort at the first FAIL instead of running the rest),
`-SkipInformational` (drop the benchmark rows), `-StrictMissing` (turn a missing ROM into a FAIL
instead of a SKIP), `-CasesDir <path>`, `-Exe <path>`, `-TimeoutSeconds <n>`.

Exit codes: **0** = no failures (skips are fine), **1** = at least one case failed, **2** = the
runner could not run (no Workshop build, no manifests, nothing matched the filter, or
`-UpdateGolden` was declined).

Prerequisite: `dotnet build Workshop/BrokenNes.Workshop.csproj -c Release`. The runner shells out to
`Workshop\bin\Release\net10.0-windows\BrokenNes.Workshop.exe` and refuses to run without it. Note
the story-ROM cases resolve against `Windows\bin\Release\...\Data\story\`, so a Release build of
BrokenNes.Windows is needed for those too.

## What each manifest covers

| File | Cases | ROMs live where | What a failure there means |
|---|---|---|---|
| `cases/story.json` | 9 | in-repo (`Windows/bin/Release/.../Data/story`) | The most portable part of the suite - these are the only ROMs that ship with the project. If these move, something in the shared emulation path changed. |
| `cases/cores.json` | 13 | in-repo story ROM | One ROM, many core stacks, so a moved hash points at a **core**, not a ROM. Includes the FIX-vs-FMC pair, the three single-core isolation cases, the default-core case, and goldens for the gimmick cores (LQ/ULQ/BFR/SPD/LOW). |
| `cases/mapper30.json` | 5 | `C:\Users\philt\Desktop\game.nes` (machine-specific -> SKIPs elsewhere) | The user's real UNROM-512 homebrew. The regression guard for the mapper-30 + unofficial-opcode work. |
| `cases/corpus.json` | 10 run + 16 skip | `SubProjects/65-o-borked/bin/Debug/net8.0/roms` (not in repo -> SKIPs elsewhere) | Mapper breadth: one representative title per **implemented** mapper, plus an explicit, reasoned SKIP entry for every unimplemented mapper the corpus contains. |
| `cases/diag.json` | 4 gated + 3 informational | in-repo + corpus | Savestate round trips (gated) and speed benchmarks (never gated). |

### The two cases worth knowing about individually

- **`mapper30-start-fix`** presses Start at frames 120 and 240 and hashes frame 600. Its output
  genuinely differs from the no-input boot case (3 distinct colours on the title screen, 6 after
  Start), so a pass proves input was delivered *and* the post-title code path ran to the same
  pixels. That code path is what the unofficial 6502 opcodes added to CPU_FIX exist for.
- **`mapper30-boot-fmc-crashes`** expects **exit code 1**. This ROM crashes CPU_FMC on frame 0 with
  `Bad opcode A7` (LAX zp), because the unofficial opcodes only ever went into CPU_FIX. It is in the
  suite so that `mapper30-boot-fix`'s green is demonstrably meaningful rather than vacuous. It will
  go red the day someone gives CPU_FMC unofficial opcodes - and updating its golden is the right
  response when that happens.

### Mapper coverage, stated honestly

`Windows/NesEmulator/board/Cartridge.cs` implements mappers **0, 1, 2, 3, 4, 5, 7, 9, 30, 33, 90,
228**. The 758 `.nes` files in the 65-o-borked corpus span 25 mappers, so **9 of them are runnable
and 16 are not**. All 16 are listed in `cases/corpus.json` as `skip` entries naming the mapper and
the ROM count, so the gap is visible in every run rather than silently absent. Mappers 33 and 90 are
implemented but have no representative in that corpus; mapper 30 is covered by `cases/mapper30.json`.

## Adding a case

1. Pick the right manifest (or add a new `cases/*.json` - the runner globs the directory).
2. Add an object to `cases`. Minimum for a framebuffer case:

```json
{
  "id": "corpus-m002-unrom-something",
  "description": "Why this case exists and what a failure would mean.",
  "kind": "romtest",
  "rom": "{corpus}/Some Game (U).nes",
  "cpu": "FIX", "ppu": "FIX", "apu": "FIX",
  "frames": 600,
  "input": "120:Start,132:",
  "tags": [ "corpus", "mapper2" ],
  "expect": null
}
```

   - `kind` is `romtest` (default), `savestate`, or `benchmark`.
   - `rom` may use a `{name}` token defined in that manifest's `roots`, an absolute path, or a
     repo-relative path.
   - `input` is `--romtest`'s script format: `frame:Buttons` steps joined by commas, buttons joined
     by `+`, an empty button list releases everything.
   - `savestate` cases take `warmupFrames` / `continueFrames` / `strict` instead of `frames`.
   - `benchmark` cases take `weight`, have no `expect`, and can never fail the suite.
   - Set `expect` to `null` and let the runner fill it in.
   - To record a ROM you deliberately are NOT testing, give the case a `skip` string explaining why
     instead of an `expect`. It shows up as a SKIP row with your reason attached. **Do not silently
     drop a ROM** - an unexplained absence is indistinguishable from an oversight.

3. Record the golden, then verify it independently:

```powershell
.\UAT\run-suite.ps1 -Filter 'corpus-m002-unrom-something' -UpdateGolden
.\UAT\run-suite.ps1 -Filter 'corpus-m002-unrom-something' -Repeat 3
```

4. Sanity-check the recorded `distinctColors`. A value of 1 or 2 means the screen is essentially
   blank and the case proves nothing useful - pick a different frame count or a different ROM.

## When it is legitimate to update goldens

`-UpdateGolden` is the "accept the new reality" button. It does not test anything: it declares
whatever the emulator does right now to be correct and overwrites the evidence of any regression
currently sitting in the working tree. It is deliberately loud - a banner, a full list of what is in
scope, and a prompt that requires you to type `UPDATE` in caps (`-Yes` skips the prompt for
scripted use). It is never the default and it should never be reflexive.

**Legitimate:**

- You intentionally changed emulator behaviour (a rendering fix, a timing fix, a new mapper) and you
  have looked at the failing diff and confirmed the new output is the one you wanted.
- You added a new case, or changed an existing case's ROM / cores / frame count / input.
- You are seeding goldens on a machine or build where none existed.

**Not legitimate:**

- The suite went red and you do not know why. That red IS the finding. Investigate it.
- You want a clean run before committing. A clean run you manufactured is worth nothing.
- You are updating "everything" to clear one unrelated failure. Use `-Filter` and move only the
  goldens you actually reasoned about.

Two safety properties are built in: `-UpdateGolden` runs every case **twice** and refuses to write a
golden whose two runs disagree (an unreproducible hash is worse than no hash), and it stamps
`goldenGeneratedUtc` into each manifest it rewrites. When you do update, **commit the manifest
change on its own**, with a message saying which behaviour moved and why - `git diff UAT/cases` is
the review surface, and a golden diff buried in a large commit is a golden nobody reviewed.

## How the goldens in this repo were produced

Recorded 2026-09-03 against the Workshop Release build of the same day, on this machine. Each was
generated from two agreeing runs, then independently re-verified across five further runs (two full
verify passes plus a `-Repeat 3` audit) - seven identical runs per case, zero disagreements. The
suite was also confirmed to actually fail: pointing `cores-binty-fix-fix-fix` at PPU_LQ and 720
frames produced

```
FAIL    cores-binty-fix-fix-fix  romtest  FIX/LQ/FIX  720  exit=0 480B5D67BD5BF30D  exit=0 B627B3B19A912A45
        > frameBufferHash: expected '480B5D67BD5BF30D', got 'B627B3B19A912A45'; framesRun: expected '600', got '720'; distinctColors: expected '12', got '8'
```

and exit code 1; reverting the edit returned it to green.

---

## Driving the app for tests

**Use the HTTP API (`lib/ApiClient.ps1`), not UI Automation, for anything the API can do.**
BrokenNes.Windows hosts ~148 loopback endpoints (`Windows/webapi/`, documented in
`Windows/webapi/README.md`). ROM loading, pause/resume, core and shader switching, memory
peek/poke, CPU registers, the PPU framebuffer, RTC blasts, quick save/load and the shell's view
mode are all reachable over HTTP - no focus, no z-order, no clicking.

The single biggest win: `/api/emulator/load-rom` loads a ROM **by path**, with no file dialog.
Every earlier UAT round fought `Emulator > Load Rom...` instead, and UI Automation's
`InvokePattern.Invoke()` on that menu item **blocks the calling thread until the modal file
dialog it opens is closed** - so the script that was meant to type a path into the dialog was
itself frozen. Days of `direct-play*.md`, `savestate.md` and `glitch-harvester*.md` retries came
out of that one behaviour. Don't re-walk into it.

```powershell
. .\UAT\lib\ApiClient.ps1

$inst = Start-BrokenNesInstance                       # launches, waits for /api/health
Show-BrokenNesEmulator   -ProcessId $inst.ProcessId   # POST /api/navigation/go-to-emulator
Load-BrokenNesRom        -ProcessId $inst.ProcessId -Path 'C:\roms\game.nes'
Start-Sleep -Seconds 5
$fb = Get-BrokenNesFramebuffer -ProcessId $inst.ProcessId -OutPng .\UAT\screenshots\frame.png
"$($fb.UniqueColors) colours, $($fb.NonBlackPercent)% non-black"
Get-BrokenNesMemory      -ProcessId $inst.ProcessId -Domain 'System RAM' -Address 0 -Length 32
Stop-BrokenNesInstance   -ProcessId $inst.ProcessId
```

Things worth knowing before you write a script against it:

- **Always scope to a PID.** The API prefers the historical fixed ports (42067/42068) but falls
  back to OS-assigned ephemeral ports when another instance already owns them, and each instance
  publishes `%LOCALAPPDATA%\BrokenNes\instances\<pid>.json`. `Get-BrokenNesInstances` reads that
  directory and drops files whose PID is dead; every wrapper takes `-ProcessId`. Talking to a bare
  port is how the 2026-08-29 sweep ended up cross-talking into another agent's window.
- **A modal MessageBox at startup can stop the API from ever binding.** `MainForm` shows the
  "Audio Warning" / DirectX-failure boxes *before* it starts the server, so on a machine where
  audio init fails nothing binds until the box is dismissed. `Wait-BrokenNesApi` clears such
  dialogs while polling (`-KeepDialogs` opts out); `Get-BrokenNesDialogs` /
  `Dismiss-BrokenNesDialogs` are there if you need them directly.
- **Check `success`, not the HTTP status.** Loading a missing ROM returns HTTP 200 with
  `{ success:false }`; a bad memory domain returns HTTP 400 with `{ success:false, error }`.
  `Invoke-BrokenNesApi` surfaces the parsed body in both cases instead of throwing.
- **`/api/rtc/*`, `/api/gh/*`, `/api/timejump/*` and `/api/imagine/*` are progression-gated** and
  answer 403 when the matching webmodule is locked in the save.
- **Never call `POST /api/save/reset`** on this machine - it wipes the real progression save.
- Still use `UiaHelpers.ps1` for what the API genuinely cannot see: native menu item presence and
  check state, WebView2 DOM, dialogs, and `Save-WindowScreenshot` for what the window actually
  renders (as opposed to `/api/ppu/framebuffer`, which is the raw NES output before shaders,
  backgrounds and scaling).

## Known constraints going in

- **BrokenNes.Windows has two UI surfaces**: a WebView2 front end (Main Menu, Deck Builder, ROM
  Manager, Options, achievements) and a native WinForms MenuStrip (the "Open Emulator" direct-play
  screen - Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help menus). The native menu is
  ordinary and easy to automate; the WebView2 screens need the technique in `UiaHelpers.ps1`.
- **ROM Manager's game list requires real, user-imported ROM files** (via a native file-open
  dialog) and is scoped to RetroAchievements-compatible titles - not usable for a from-scratch
  automated test without either supplying ROMs externally or driving that native dialog. The
  "Open Emulator" direct-play screen's own Emulator > Load ROM works against any local `.nes` file,
  including the game's own bundled `Data/story/*.nes` test ROMs - use that path for gameplay tests.
  From a script, do it as `Load-BrokenNesRom` (`POST /api/emulator/load-rom`) rather than through
  that menu item: same code path in the app, no modal dialog to fight.
- **CPU_FIX/PPU_FIX/APU_FIX are locked behind the progression/card-ownership system** in
  BrokenNes.Windows specifically (confirmed by reading `MainForm.Config.cs`'s menu-population logic
  and `MainForm.Progression.cs`'s `IsCpuCoreUnlocked`/`IsPpuCoreUnlocked`/`IsApuCoreUnlocked`, which
  check `GameSave.OwnedCpuIds`/`OwnedPpuIds`/`OwnedApuIds` with no default/free grant for FIX). No
  card/unlock definition was ever added for the FIX family, so a normal player's save can never
  unlock them - they simply don't appear in the CPU/PPU/APU menus. This is NOT a bug in FIX itself
  (Workshop/Web/WebLite all select it fine, since none of them have a progression/ownership system)
  - it's a real gap in the FIX rollout that only shows up in the one build that gates cores behind
  unlocks. `config.ShowLockedItems` (an existing settings toggle) should reveal FIX as `[Locked]`
  without granting access - a live way to confirm this diagnosis without code changes.
- **[FIXED 2026-09-02 - kept for history]** Multiple independent `BrokenNes.Windows.exe` instances
  CAN launch simultaneously (no single-instance lock), and each now gets its **own** API endpoint:
  the server prefers 42067/42068 and falls back to OS-assigned ephemeral ports when they are
  taken, publishes `%LOCALAPPDATA%\BrokenNes\instances\<pid>.json`, and injects
  `window.BROKENNES_API_BASE` into its own WebView2. Verified live with two concurrent instances
  (42067/42068 and 56700/56701, each resolvable by PID). "Open Emulator" therefore works on every
  instance and the areas below are safe to parallelise again. The original report follows.

  _Original report:_ discovered
  during the first parallel UAT round (12 concurrent instances): the WebView2-to-native handoff for
  every button that isn't a plain client-side `window.location.href` navigation (Open Emulator,
  Controller Config, and likely others) goes through a local HTTP API
  (`Windows/webapi/WebApiServer.cs`) hardcoded to loopback port 42067/42068 with no per-instance
  uniqueness. Only the first-launched process can bind that port; every other simultaneously-running
  instance's bind attempt fails, and the failure is silently swallowed
  (`MainForm.Initialization.cs`: "Don't show error to user, API is optional") with no retry - one
  agent additionally found `WebApiServer.cs` sets `_host = app` *before* `await _host.StartAsync()`,
  so `IsRunning` reports true even when the bind actually failed. Because the port isn't
  process-scoped, a "losing" instance's WebView2 still successfully reaches *some* process's API -
  just not necessarily its own - so clicking Open Emulator on a losing instance can silently
  no-op OR cross-talk into a completely different, unrelated instance's window (confirmed live: one
  agent's diagnostic HTTP call visibly flipped a different instance's view mode). This is a real,
  independently-worth-fixing product bug (bind an OS-assigned ephemeral port and inject it into the
  WebView2 page instead of hardcoding 42067 everywhere) - it would also bite a real user who happens
  to launch the app twice, not just parallel UAT. **Practical implication for this harness: run any
  area that needs "Open Emulator" with EXCLUSIVE access (no other BrokenNes.Windows.exe instance
  alive) - sequentially, not in a `parallel()`.** Areas that only use client-side WebView2
  navigation (Deck Builder, ROM Manager, Options, Credits) are unaffected and remain safe to
  parallelize freely.
- **`Save-WindowScreenshot` now uses `PrintWindow` (PW_RENDERFULLCONTENT), not `CopyFromScreen`** -
  the first parallel UAT round found that `CopyFromScreen` on a window's on-screen rectangle
  silently captures whichever window is topmost AT THOSE PIXELS, which is a different instance's
  content whenever multiple overlapping windows share the same default launch position. `PrintWindow`
  renders the target HWND's own content directly regardless of z-order/overlap and is safe to use
  under heavy parallelism.
- The Deck Builder / progression save file observed in the first UAT round was NOT a fresh/empty
  save - it already had 136/138 cores owned, 8 achievement stars, Level 7 (apparently shared/
  persisted from prior dev testing, not per-instance-isolated). Don't assume a fresh-save empty
  state when writing test scripts against this machine.
