# UAT Findings: Direct-Play ROM Loading and Basic Gameplay (Round 2)

**Area:** direct-play ROM loading and basic gameplay
**Round-1 file:** `UAT/findings/direct-play.md` (fully blocked — "Open Emulator" never navigated
under any input method, with exclusive-access parallelism identified as the likely cause)
**Instances used this round:** three independent `BrokenNes.Windows.exe` runs in sequence, each
launched with exclusive access (verified no other instance running, port 42067 free before each
launch) — PIDs 19628, 12944, 9716
**Date:** 2026-08-31

## Summary

**Exclusive access fully fixed the round-1 blocker.** "Open Emulator" navigated to the native
MenuStrip screen on the very first click, every single time (3/3 fresh launches), confirming
round 1's diagnosis: the WebView2→native handoff's hardcoded-port HTTP API
(`Windows/webapi/WebApiServer.cs`) is what silently broke the button under concurrent instances,
not a defect in the button/handler itself. With sole ownership of ports 42067/42068 verified
before each launch, "Open Emulator" is not a real defect — round 1's finding should be closed as
an environmental/parallelism artifact, not a product bug.

Steps 3–6 of the assigned script were completed: the native MenuStrip screen loads correctly,
`Emulator > Load Rom...` opens a real native file-open dialog that successfully loads
`Data\story\page1_binty.nes` (display changes from the idle Mandelbrot placeholder to actual game
content), and basic keyboard input reaches the emulator. **However, this round surfaced a new,
serious, independently-reproducible defect that round 1 never got far enough to see: sending a
quick burst of keyboard input (arrow keys + Z + X + Enter, each ~150ms apart) while a ROM is
loaded and playing causes the entire `BrokenNes.Windows.exe` process to terminate silently — no
crash dialog, no WerFault, no Application-log error entry of any kind.** This reproduced 3 out of
3 times under that exact rapid-fire timing, and did **not** reproduce when the identical keys were
sent individually with larger (300ms+) real-world gaps between them across separate invocations
(arrows, Z, X, and Enter all individually confirmed harmless on their own, both on the idle screen
and mid-gameplay). This is a high-priority, real product bug in the direct-play gameplay path —
exactly the kind of input a real player mashing buttons during gameplay would produce.

## Step-by-step

**Step 1 — Launch, dismiss dialogs, click "Open Emulator".** PASS, all 3 launches.
- Defensive pre-flight confirmed clean each time: `Get-Process -Name BrokenNes.Windows` empty,
  `Get-NetTCPConnection -LocalPort 42067` empty, before every launch.
- Each launch showed an **Audio Warning** dialog first (`Audio initialization failed: BadDeviceId
  calling waveOutOpen — The emulator will run without sound`) — not mentioned in round 1's
  write-up (round 1 also hit "Health Warning" directly; this round consistently saw Audio Warning
  first, then Health Warning). Dismissed via its "OK" button (plain WinForms dialog, trivial UIA).
  Screenshot: `direct-play-r2-00-audiowarning.png`.
- Health Warning dismissed via `Acknowledge health warning` (`direct-play-r2-02-mainmenu.png`
  shows the resulting Main Menu).
- Confirmed port ownership: `Get-NetTCPConnection -LocalPort 42067/42068` showed both bound to
  our own PID once the Main Menu loaded.
- Enumerated Main Menu accessible names — identical to round 1's documented set (`Open Emulator`,
  `Open Deck Builder`, `ROM Manager`, `Options`).
- Clicked `Open Emulator` via `Click-Element` (InvokePattern). **Result: immediate, successful
  navigation to the native MenuStrip screen (Emulator/Config/Tools & Activities/SHADER/APU/CPU/
  PPU/Help) on the very first attempt, every one of the 3 separate launches.**
  Screenshot: `direct-play-r2-03-afteropenemulator.png` — shows the idle "Continue?" thumbnail and
  an animated Mandelbrot placeholder in the display area (this animates continuously as an idle
  screensaver when no ROM is loaded).

**Step 2 — Open "Emulator" menu, enumerate items.** PASS.
- `Get-NativeMenuBar` + `Open-NativeMenu` found and opened the menu cleanly.
- Enumerated 12 items: `Load Rom...`, `Close Rom`, `Recent Roms`, `Pause/Resume`,
  `Reset Emulator`, `Quick Load State`, `Quick Save State`, `Load State...`, `Save State...`,
  `Take Screenshot`, `Open Emulator Folder`, `Exit`.
- **Note on automating this specific menu:** a plain `InvokePattern.Invoke()` call on `Load
  Rom...` reliably **hangs/times out client-side** (confirmed: one direct call threw a COM
  "Operation timed out (0x80131505)" after some delay; a second identical call hung past a 30s
  tool timeout with no exception at all). This is because the click handler calls
  `OpenFileDialog.ShowDialog()` synchronously on the UI thread, which pumps a nested modal loop —
  a synchronous cross-process UIA `Invoke()` call blocks waiting for that method to return, which
  it won't until the dialog closes. **Keyboard-driven navigation (open menu, `{DOWN}`, `{ENTER}`)
  does not have this blocking problem** since `SendKeys` posts input asynchronously rather than
  waiting on the provider call, and is the reliable way to activate a menu item that opens a
  modal dialog. Also note: the file-open dialog is **not a normal top-level HWND** — it doesn't
  show up in `Get-NetTCPConnection`-style top-level enumeration via
  `AutomationElement.RootElement.FindAll(Children, pidCondition)`, and `FindWindow` (by class
  `#32770` or by title) can't find it either. It only appears as a **descendant of the main
  "BrokenNes" window** in the UI Automation tree (`Get-AppRoot(...).FindFirst(Descendants, name
  condition)` finds it fine) — worth adding to `UiaHelpers.ps1`/README for future testers.
- **Secondary observation (test-methodology risk, not necessarily a product bug):** opening the
  dropdown and keyboard-navigating into `Load Rom...` was noticeably flaky under automation —
  roughly half of attempts across the session produced no visible effect at all (menu closed with
  nothing happening, no dialog, animation just continued), requiring a retry. Once a retry
  succeeded, it was consistently reliable for that attempt. This could be genuine automation
  timing (SendKeys racing the menu's own open animation) rather than an app defect — a human
  clicking through this menu would not likely hit it — but is flagged here since it consumed
  significant time and could indicate a minor real timing sensitivity in the dropdown's input
  handling worth a quick look.

**Step 3 — Load `Data\story\page1_binty.nes`.** PASS (after working around the automation
flakiness above).
- The native "Select a NES ROM" file dialog opens already browsing to
  `Windows\bin\Release\net10.0-windows\win-x64\Data\story`, with `page1_binty.nes` visible at the
  top of the list — convenient, and confirms the dialog's default start directory is sensibly
  scoped to the game's own ROM folder.
- Located the actual editable `ControlType.Edit` named "File name:" (distinct from a
  `ControlType.Text` label and a `ControlType.ComboBox` also both named "File name:" — three
  different elements share that name in this dialog; the Edit one is the only one supporting
  `ValuePattern`). Set its value to the full path, then submitted via `{ENTER}`.
  Screenshot with path populated: `direct-play-r2-18-filenameset.png`.
- Confirmed successful load 3 times total across the 3 launches — each time the display
  transitioned from the idle Mandelbrot placeholder to the actual story-page artwork (a boy
  playing a NES, captioned "Continue?").

**Step 4 — Before/after screenshots.** PASS.
- Before: `direct-play-r2-03-afteropenemulator.png` (idle Mandelbrot + "Continue?" thumbnail).
- After: `direct-play-r2-24-romloaded.png`, `direct-play-r2-27-romloaded3.png`,
  `direct-play-r2-32-romloaded4.png` — display clearly changed from idle placeholder to actual
  loaded ROM content (large-format render of the story page) each time.

**Step 5 — Basic input sanity check (arrows, Z, X, Enter).** PASS individually, **FAILED /
crashed the process when sent rapidly.** See "The crash, in detail" below.

**Step 6 — Pause/Reset.** PARTIAL PASS.
- `Pause/Resume` (menu item 4): keyboard-activated on the idle screen; two screenshots taken ~2s
  apart showed an **identical, frozen Mandelbrot frame** (versus the constant color/rotation
  drift visible in every other pair of idle screenshots taken seconds apart), strong visual
  evidence Pause actually works. Screenshots: `direct-play-r2-15-pauseA.png` /
  `direct-play-r2-15-pauseB.png`.
- `Reset Emulator` was not explicitly isolated/tested this round — deprioritized once the process-
  exit bug was found, to spend remaining time nailing down a clean repro of that instead. Flagging
  as not-yet-covered rather than passed or failed.

**Step 7 — Report.** This document.

## The crash, in detail

**Reproduction (3/3):** With a ROM loaded and the game canvas focused (confirmed via a real mouse
click on the canvas before sending any keys), sending this exact sequence — each key via a
separate `SendKeys::SendWait` call, ~150ms apart, all within one PowerShell invocation —
terminates the entire process:

```
{LEFT}  (~150ms)
{RIGHT} (~150ms)
{UP}    (~150ms)
{DOWN}  (~150ms)
z       (~150ms)
x       (~150ms)
{ENTER}
```

Immediately after, `Get-Process -Id <pid>` returns nothing — the process is completely gone, not
hung, not showing a dialog. Checked and ruled out each time:
- No `WerFault.exe` process appears (rules out an unhandled-exception crash reaching the default
  Windows Error Reporting handler).
- No new Application-log Error/Critical event appears in the several minutes around the exit
  (`Get-WinEvent -FilterHashtable @{LogName='Application'; Level=1,2,3}` — empty).
- This is therefore a **silent, clean-looking exit** (consistent with something in the
  input-handling path calling `Application.Exit()`/`Environment.Exit()` or an unhandled exception
  in a context where the default handler is suppressed) — not a segfault/access-violation-style
  crash.

**Non-repro controls (2/2 survived):** The identical set of keys, sent individually as separate
PowerShell/SendKeys invocations (each with the natural ~300–500ms+ latency of spinning up a new
process per call, plus explicit checks in between), did **not** crash the app — confirmed alive
and `Responding=True` after every single key, on two separate ROM-loaded sessions, including the
literal same key set (arrows, Z, X, Enter) tested one at a time back-to-back. The same rapid
7-key/150ms-apart sequence was also sent once on the **idle** screen (no ROM loaded) and did
**not** crash the process — so this is specifically tied to rapid input while a ROM is actively
loaded/playing, not rapid input in general.

**Conclusion:** this strongly points to a real, timing-sensitive bug in the direct-play gameplay
input path — plausibly an input-queue/buffer race that only manifests when multiple key
transitions land within the same or adjacent emulation frames. This is exactly the kind of input
pattern a real player would produce (mashing a d-pad + A/B during gameplay), so it is not just a
theoretical/automation-only concern. Recommend treating as high priority: total, silent process
loss during gameplay is a severe UX regression and a real data-loss risk (any unsaved state is
gone with no warning).

## What passed

- Exclusive-access launch/pre-flight checks (`Get-Process`, `Get-NetTCPConnection`) — clean every
  time, as instructed.
- Health Warning and Audio Warning dialogs, dismissal.
- **"Open Emulator" navigation — 3/3, immediate, first-try success under exclusive access.** This
  directly confirms round 1's root-cause diagnosis (hardcoded-port API contention under
  parallelism) and closes that finding as environmental, not a product defect.
- Native MenuStrip screen loads correctly with all 8 top-level menus and the `Emulator` dropdown's
  12 items enumerable via UIA.
- `Emulator > Load Rom...` opens a real native "Select a NES ROM" file dialog, correctly
  pre-scoped to the game's `Data\story` folder.
- Loading `page1_binty.nes` by typing the full path into the filename field and pressing Enter —
  succeeded 3/3 attempts (once flakiness in reaching the dialog itself was worked around), each
  time producing a clear, visually-confirmed display change from idle placeholder to game content.
- Individual keyboard inputs (each of Left/Right/Up/Down/Z/X/Enter) — all confirmed harmless in
  isolation, both pre- and post-ROM-load.
- `Pause/Resume` — visually confirmed working (animation freeze reproduced via two timed
  screenshots).
- General process stability *outside* the specific rapid-input trigger — the app survived being
  backgrounded, re-foregrounded, having stray Escape/Alt-menu key sequences sent at it, and one
  instance of a leftover orphaned Explorer tooltip window, without ever hanging or becoming
  unresponsive (`Responding=True` throughout).

## What failed

- **New, high-priority defect (this round's main finding): sending a quick burst of mixed
  keyboard input (arrows + Z + X + Enter, ~150ms cadence) while a ROM is loaded silently
  terminates the entire `BrokenNes.Windows.exe` process, with no crash dialog, no WerFault, and no
  Application-log entry.** Reproduced 3/3 under that timing; did not reproduce under slower,
  one-key-at-a-time timing (2/2 survived) or on the idle/no-ROM screen (1/1 survived). See "The
  crash, in detail" above for exact repro steps.
- Minor: automating `Emulator > Load Rom...` via keyboard navigation was flaky (roughly half of
  attempts produced no visible effect, requiring a retry) — most likely an automation-timing
  artifact rather than a user-facing bug, but flagged since it was never explained and cost
  significant session time; worth a quick look in case it reflects a real timing sensitivity in
  the dropdown.

## What I could not test / verify (and why)

- `Reset Emulator` menu item — not explicitly isolated this round; time was reprioritized to
  nailing down a clean, controlled repro of the process-exit bug once it was found. Should be
  covered in a future pass.
- Whether the process-exit bug is specific to `page1_binty.nes` (a story/splash-image ROM, which
  may have unusual/minimal internal input handling compared to a "real" game ROM) or general to
  any loaded ROM — only this one ROM was tested per the assigned script. Worth re-testing with a
  different, more typical gameplay ROM to see if the same rapid-input pattern reproduces there
  too, and to rule out `page1_binty.nes`'s own game logic (rather than BrokenNes's core/input
  layer) as the culprit.
- The exact internal cause of the silent exit (no crash dump/stack trace was obtainable from
  outside the process in a Release build) — flagging as the top candidate for a source-level
  investigation (input queue handling in the direct-play view, `MainForm`'s keyboard event
  handlers, or the active core's input-polling code).
- Save/Load State menu items — not attempted this round (deprioritized in favor of the crash
  investigation).

## Files

- Findings: `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\findings\direct-play-r2.md`
  (this file); round-1 file for continuity:
  `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\UAT\findings\direct-play.md`
- Key screenshots (`UAT/screenshots/`):
  - `direct-play-r2-00-audiowarning.png`, `direct-play-r2-02-mainmenu.png` — startup dialogs.
  - `direct-play-r2-03-afteropenemulator.png` — proof Open Emulator navigated (idle state).
  - `direct-play-r2-16-dropdown-copyfromscreen.png`, `direct-play-r2-18-filenameset.png` — the
    native Load Rom dialog, including the path typed into the filename field.
  - `direct-play-r2-24-romloaded.png`, `direct-play-r2-27-romloaded3.png`,
    `direct-play-r2-32-romloaded4.png` — confirmed ROM-loaded display state (3 separate loads).
  - `direct-play-r2-15-pauseA.png` / `direct-play-r2-15-pauseB.png` — Pause/Resume frozen-frame
    evidence.
  - (Process-exit events produced no "after" screenshot by definition — the window is gone;
    confirmed instead via repeated `Get-Process` checks immediately following each rapid-input
    sequence.)
