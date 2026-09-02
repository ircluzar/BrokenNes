# BrokenNes UAT harness

A side-project sanity-testing harness for the Windows desktop app and the Web build. Deliberately
kept separate from `Windows/`, `Web/`, `WebLite/`, and `Workshop/` - nothing in those shipped
projects references this folder, and nothing here modifies them. Just PowerShell scripts and
findings.

## What's here

- `lib/UiaHelpers.ps1` - reusable UI Automation helpers, with the non-obvious quirks of automating
  BrokenNes.Windows's WebView2-hosted screens written up as comments (read this file first).
- `findings/` - one markdown file per test pass, written by whichever agent/session ran it.
- `screenshots/` - visual evidence referenced from findings files.

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
- **Multiple independent `BrokenNes.Windows.exe` instances CAN launch simultaneously (no single-
  instance lock), but only ONE of them can actually use "Open Emulator" at a time.** Discovered
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
