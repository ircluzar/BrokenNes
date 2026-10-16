# Round 5, agent `inputs`: d-pad protection, and the plain name BrokenNes2

Date: 2026-10-17. Two small tasks from the user (brief: `round5-brief.md`). Branch `worktree-agent-a6e5b86290dff809f`, never pushed. Everything below was run; "not tested" says what was not.

## 1. D-pad protection

> Pressing opposite directions at once is impossible on real d-pads, so protection is default behaviour, and can be disabled in the input controls.

### Behaviour

When Left and Right are both held, neither is sent; when Up and Down are both held, neither is sent (neutral on that axis). Up+Down+Right sends Right (the pair cancels, the lone direction stays); all four sends neutral. Everything else (A, B, Start, X, L ...) is untouched. Letting go of one key of a pair hands the other back at once: the apps keep the raw state and only the answer is protected.

It applies to **human input only** (keyboard, gamepad and stick, touch controller, the plugin's on-screen pad), and to the keyboard and the pad **merged** (the keyboard's Left plus the stick's Right cancel too). It does **not** apply to the Web API's set-buttons, movie / TAS playback, Workshop runs or any replay tool.

### Where it lives (one place per app)

| App | The one pure function | Called from (once) | Setting, default OFF = protected | Stored in | UI |
|---|---|---|---|---|---|
| Desktop (NES, SNES, Game Boy, Master System, Game Gear, Genesis: every console reads `InputManager`) | `NesEmulator.Systems.DpadGuard.Resolve` / `ResolveNesOrder` in `Windows/NesEmulator/systems/DpadGuard.cs` (shared tree) | `InputManager.GetButton` (`Windows/InputManager.cs`); `Pad()` serves the 12-button session path and `MainForm.ToPad` calls it | `PlayerControllerConfig.AllowOppositeDpad` (JSON `allowOppositeDpad`), per player | `config.json` (`EmulatorConfig.PlayerControllers`); a config without the field reads as protected | tick box "Allow opposite d-pad directions" in `ControllerConfigWindow` (window grown 60 px so all 12 rows still show) |
| Lite | `window.dpadGuard.resolve` in `WebLite/wwwroot/lib/dpadGuard.js` (the web twin of the C# rule) | `nesInterop._sendPad`, the funnel for the configured keyboard, the legacy keys, the gamepad poll and the touch controller | `InputSettings.AllowOppositeDpad`, reaches the page as `cfg.allowOppositeDpad` | the browser's IndexedDB `input_settings_v1`; a save without the field reads as protected | tick box on the Input settings page (`/input`, `#allow-opposite-dpad`) |
| Minimal Web build (`Web/`) | the same 4 lines inline in `Web/wwwroot/lib/nesInterop.js` (`_sendPad`) | its keyboard handler | none: that build has no settings page, protection is always on | (none) | (none) |
| FL plugin | `DpadGuard.Resolve` (the same C# file, linked through `Core`) | `Inputs.Human`, called by the editor's poll (`Bn2Editor.PollInputs`) for keyboard + gamepad + on-screen pad together | `Inputs.AllowOpposite` | `InputAllowOpposite` in the plugin's remembered prefs (`Prefs.cs`); an install or project before the setting reads as protected | tick box at the bottom of the Inputs window (the window grew 40 px); Reset to defaults protects again |

Files touched outside my domain: `Windows/NesEmulator/board/Emulator.cs` (Lite's shell: one added line passing `allowOppositeDpad` to JavaScript), `Windows/Models/InputConfig.cs` (Lite's settings class: the field), `Windows/MainForm/MainForm.Consoles.cs` (`ToPad` now calls `InputManager.Pad()`), `Workshop/*` (the test, two links, one dispatch line).

Not covered on purpose: Workshop's own interactive players (`--snes`, `--gb`, the Workshop form) have fixed keyboards and no input settings; they are developer tools, not the product, and their direction handling is unchanged.

### Tests, with numbers and proofs of failure

**A. `Workshop --input-test`** (new CLI, `Workshop/InputTestCli.cs`; run from the repo root): **32 checks, 32 pass.**
- the pure function: all 16 combinations of Up, Down, Left, Right, protected and unprotected, against a table written out by hand (not derived from the code); all **32768** pad values for the properties (only the direction bits can change, they follow the table, idempotent, allow = identity); the NES-order list form for 16 combos x with/without the other buttons x protected/not; short lists left alone;
- the **real `InputManager`** (linked into Workshop) driven with simulated keys through its `KeyProbe` seam and the real `PlayerControllerConfig`: the 16 combinations through `GetButton` and `Pad()`, protection on and off; the eight other buttons survive a mashed pad; a press / release sequence (Left, +Right, Left let go, +Up, +Down, ...) gives `Left | neutral | Right | Up+Right | Right | neutral`; the setting is read live from the config; the legacy key map is protected; two players with different settings;
- the config JSON: new configs are protected, a config **with no field** (an old save) loads protected, the field round-trips as `allowOppositeDpad`, a manager built on the old config is protected;
- the **real `ControllerConfigWindow`** (created off screen): the box exists, is unticked by default, ticking it changes what the manager reads at once, Cancel restores, a saved ticked config opens ticked (Reset to defaults asks with a message box and is not driven); `--shot` saves the window (checked by eye: all 12 rows and the box fit);
- recorded input is untouched: the real NES controller port takes Up+Down+Left+Right (`0xF0` back, and a game reading `$4016` sees all four), and the real Genesis pad (`MdPads`) shows all four lines low;
- the caller scan: only `Windows/InputManager.cs`, `Plugin/BrokenNes2.Plugin/Bn2Input.cs` and `WebLite/wwwroot/lib/nesInterop.js` call the guard, once each and they do call it; no other file among 705 scanned (not the API injector, a movie player, a Workshop run, a core) does.
- **Injected bugs, each flagged, then reverted and the run clean again:** M1 the vertical rule removed (7 checks failed); M2 `InputManager` asks the guard with allow=true (7 failed); M3 the config default flipped to unprotected (4 failed); M4 a movie player (`TasCli`) starts calling the guard (the caller scan failed, naming `Workshop/TasCli.cs`); M5 the NES controller port itself cancels opposites (2 failed: the recorded frame no longer arrives as set).

**B. Lite: `UAT/lite/test-dpad-guard.mjs`** (node 22+; Edge/Chrome over the DevTools protocol through `litelab.mjs`): **32 checks, 32 pass** (`--publish <dir>` for the last 11).
- pure (5): the real `dpadGuard.js` in a VM, all 16 combinations with and without the other buttons, the raw list kept and a copy returned, only a strict `true` allows opposites, short lists and null;
- wiring (4): in `nesInterop.js` every `invokeMethodAsync('UpdateInput...')` is inside `_sendPad`, which calls the guard once; `index.html` loads the guard first; the minimal Web build sends only through its protected `_sendPad`;
- real browser (12), the real `nesInterop.js` and `dpadGuard.js` with a recording stand-in for Blazor's call-back: real key events (configured keyboard: Left, +Right neutral with the raw state keeping both, Right let go gives Left back, +Up+Down, A key still passes), the setting on, off again, a config with no field, the legacy keys with no configuration at all, a gamepad (d-pad Left+Right; the stick left with d-pad Right), a two-finger touch (Left+Right neutral, the second finger sliding off gives Left back);
- the real Lite site (11), a **non-AOT publish of this worktree** (`dotnet publish WebLite -c Release`): a fresh profile is protected with real key events; `/input` has the box, unticked; ticking and Save store `AllowOppositeDpad: true` in IndexedDB; the game page applies it and real Left+Right now both arrive; reopening shows it ticked; unticking protects again; an old saved setting with no such field is protected; no script errors.
- **Injected bugs, each flagged:** L1 `_sendPad` hands on the raw state (8 failed); L2 the vertical rule removed from `dpadGuard.js` (3 failed); L3 the touch controller talks to the emulator behind the funnel (the wiring check and the touch check failed); L4 the setting ignored (7 failed); L5 the minimal Web build loses its guard (the wiring check failed). The site part against an **older Lite publish** (`bn_sega_c4b_lite_out`, before this work): the fresh-profile checks failed (no guard) and the missing box ended the run with a failure.

**C. Plugin selftest, new check `dpad`** (the real editor; real mouse messages; the keyboard simulated through editor hook 90, which makes the bound keys of a pad mask "down" for the real `Inputs.PollKeyboard` path): the 16 combinations from the keyboard protected (and the editor's display equals what the emulator got); A and Start survive Left+Right; the keyboard's Left plus the mouse held on the on-screen Right is neutral and letting go gives Left back; the box is clicked with the real mouse (it changes pixels), the setting is applied, remembered (`InputAllowOpposite=1` in the prefs file) and all 16 combinations pass unchanged; unticked again; Reset to defaults protects again; a prefs file with no line, `=1`, `=0` is read as protected / allowed / protected (editor hook 92 re-reads it); a pad set from outside (hook 44) with all four directions reaches the emulator as set. **Pass.** Injected bugs, each flagged: P1 `Inputs.Human` bypasses the guard; P2 the poll no longer calls it; P3 Reset to defaults leaves opposites allowed; P4 the guard also applied to a pad set from outside; P5 the setting not remembered; P6 an old install (no line) reads as allowed.
- Not tested for the plugin: a real gamepad (none attached; the gamepad shares the keyboard's merge point, so the guard sees it, but no test pushes a real XInput state); a real keyboard in a real FL.

### Not tested

- Real key presses in the running desktop window (the keyboard path was driven through the real `InputManager.KeyProbe` seam instead; the real exe was only smoke tested). A real XInput pad and stick on the desktop. Lite on a phone (a touch screen was emulated through CDP touch events) and the AOT build of Lite (the non-AOT publish was tested; the JavaScript is identical).
- Workshop's interactive players (`--snes`, `--gb`, the Workshop form) are unchanged (see above).

## 2. The plain name: BrokenNes2

The VST is **BrokenNes2**. `grep -ri` for the old brand over the repository (excluding `.claude`, `bin`, `obj`, `.git`) finds only the migration code (`OldRegistryPath` in `Plugin/BrokenNes2.Plugin/Prefs.cs`) and the lead's own brief (`docs/projects/round5-brief.md`, which states the task).

What changed: the plugin's display, long and short name (`Bn2Plugin.DisplayName` = `BrokenNes2`; the host test `load` and the log check now expect it); the editor header and the About window title (drawn as `Broken` + `Nes2`, the old prefix and the `::` are gone); the window class (`BrokenNes2Editor`); `Product` / `Company` in the csproj (`BrokenNes2` / `BrokenNes`); the installer texts (`InstallVstDialog`, `InstallVstFlow`, `PluginInstaller`); `Plugin/README.md`, `Plugin/CLAUDE.md`, `UAT/plugin/README.md`. The DLL, folder and FL identity are unchanged (`BrokenNes2_x64.dll`). In the two c4b files only `Bn2Plugin.cs` was touched (line 1 comment and the `DisplayName` constant); `Emulator.cs` untouched. Also `Bn2Params.cs` (one doc comment).

### Settings migration

The remembered settings moved to `HKCU\Software\BrokenNes\BrokenNes2`. `Prefs.MigrateOnce` runs the first time a settings store is read or written in a process: **if the new key has no values, every value of the old key is copied over once; the old key is only read (opened read-only, so it is not even created), never changed, emptied or deleted.** A new key that already has values is left alone. REG_SZ and REG_DWORD values both come across (as text, which is how `Prefs.Get` returns both). In file mode (tests) the same rule runs between `BROKENNES_PREFS_FILE` and an optional `BROKENNES_PREFS_OLD_FILE`; the registry code can be pointed at test keys with `BROKENNES_PREFS_REGKEY` / `BROKENNES_PREFS_OLD_REGKEY`.

**Test: selftest check `prefs`** (`SelfTest.Prefs.cs`, editor hooks 93 = read a setting, 94 = write one). Fake old **files**: values carried to the new store, old file byte-identical (SHA-256), a new setting lands in the new store only, a non-empty new store is not overwritten, no old store means nothing happens and nothing is created. The **real registry code on test keys** under `HKCU\Software\BrokenNes_Test\<guid>` (deleted afterwards): a fake old key with three REG_SZ and one REG_DWORD value is carried over exactly (names and text), the old key is unchanged (4 values, the DWORD is still a DWORD) and receives nothing new, a non-empty new key is not overwritten or topped up, and with no old key nothing appears and the old key is not created. **Pass.** Injected bugs, each flagged: Q1 the migration never runs; Q2 a non-empty new key overwritten; Q3 reading the old key creates it; Q4 DWORD values not carried over; Q5 the file migration overwrites a non-empty store; Q6 the migration writes a marker into the old key.
Not tested: the two production paths themselves (constants in `Prefs.cs`; the real old key is the user's own, so nothing here wrote to it or to the real new key). On this machine the old key holds one value (`AboutRevisionSeen`); the first time the new DLL runs it will be copied across.

## Verification of the finished state

(See the table at the end, filled from the final runs.)

## Decisions

- Protection is **per player** (in `PlayerControllerConfig`), not one global flag: the controller window edits one player at a time with Save / Cancel / Reset, and the `InputManager` already holds that object, so the setting is live and Cancel restores it for free.
- The guard works on the **merged** pad (keyboard + gamepad + stick + on-screen pad), not per device, because Left on the keyboard and Right on the stick is just as impossible on a d-pad.
- Lite gets a JavaScript twin rather than going through C#, because the JavaScript layer is the one that knows what is human (keys, pads, touch); the C# entries `UpdateInput` / `UpdateInputForPlayer` stay raw so tests and any future programmatic caller are untouched. The twin is a 10-line function, tested for the same 16 combinations; the minimal Web build, which has no settings page, carries the 4 lines inline, always on.
- The caller scan in `--input-test` is deliberate: the rule "never on recorded input" is enforced by a test that fails when a new caller appears.
- The plugin keeps its test seam (`Inputs.KeyProbe`) reachable only through editor hook 90; `InputManager.KeyProbe` is a public property, null in the app.

## Open problems / next

- The first selftest run of a session once failed `inputs` ("the on-screen A button did not press A") and passed on rerun and in three further runs: the existing check is sensitive to desktop activity (other agents were building); not caused by this work (the A button is not a direction).
- If the user wants one global switch for both players, the setting can move to `EmulatorConfig`; the pure function and the plumbing do not change.
- Workshop's interactive players could take the guard with one call each if wanted.
