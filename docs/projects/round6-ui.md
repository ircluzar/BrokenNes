# Round 6, `ui`: the editor's tabs (Emulator | Instruments | Presets)

Date: 2026-10-17. Domain: the tab framework and window widening in `Bn2Editor.cs`, the Instruments and Presets tab shells, the shared widgets, the self-test coverage. Brief: `docs/projects/round6-instruments-brief.md`.
Branch `worktree-agent-adeff1a703f4f60a2`, builds in `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r6_ui`. Nothing pushed, nothing merged into `main`.

## What exists

| Piece | Where |
|---|---|
| tab strip on the header rule above the right column, window 904 x 664 on Emulator, 1324 x 664 (`UiLayout.WideExtra` = 420) on Instruments and Presets, host told | `Plugin/BrokenNes2.Plugin/Ui/Bn2Editor.Tabs.cs` (a `partial` of `Bn2Editor`), `UiIds.cs` (`UiLayout`) |
| the contract: `IInstrumentModelProvider`, `IPresetLibrary`, `UiServices`, the value types | `Ui/Contracts.cs` |
| where the real models register (partial methods, in the owners' own files) | `Ui/UiBoot.cs` |
| demonstration models (30 instruments of every field kind, a seeded corruptor with history, a 14-preset library); only the self-test installs them | `Ui/StubModels.cs` |
| widgets: scrolling list and scroll bar, slider, stepper, bit toggle, text input, hex field, waveform view with loop markers; the drawing surface | `Ui/ScrollList.cs`, `SliderWidget.cs`, `StepperWidget.cs`, `BitToggle.cs`, `TextInput.cs`, `HexField.cs`, `WaveView.cs`, `UiCanvas.cs` |
| the two tab panels | `Ui/InstrumentsPanel.cs`, `Ui/PresetsPanel.cs` |
| control id map, fault switches | `Ui/UiIds.cs` |
| the test bench: `editor-shot --tab/--demo/--freeze/--click/--chars`, `bmpdiff`, `editor-golden`, `HostSim.EditorResized`, `Win32Host` keys / `WheelAt` / `FitFrame` | `Plugin/BrokenNes.FruityHost`, `Plugin/BrokenNes.SynthHost/HostSim.cs` |
| the standalone synth's panel follows the editor's size | `SynthRack.EditorResized`, `Windows/Synth/SynthForm.cs` (`FitEditorPanel`) |
| tests `ui` (22 scenarios) and `ui-proof` | `Plugin/BrokenNes.FruityHost/SelfTest.Ui.cs`, registered in `SelfTest.cs` |

Edits to the shared files are small and additive: `Bn2Editor.cs` (the class is `partial`; `Kind.Ui`; an `if (tab == 0) { ...right column untouched... } else DrawTabContent(...)` around the right column with no line of it re-indented; the Reset block under `tab == 0`;
`Mix(UiSignature())`; the back buffer at the widest size; one-line routes in `OnMouse`, `WndProc` (`WM_CHAR`, `WM_KEYDOWN`, capture lost), `Test`, `PollInputs`), `Fhd.EditorResized` in `FruitySdk.cs`, `HostSim.cs`, `SynthRack.cs`, `SynthForm.cs`.
`Bn2Plugin.cs` is not touched. No FL parameter was added (`Bn2Params.cs` untouched, the `params` test passes).

## The window size: how FL editors resize (the contract question)

`IFruityEditor` (`Plugin/BrokenNes.Fruity/FruityPluginBase.cs`) has only `Hwnd` and `Destroy`, and `Fhd` had only `ParamMenu` and `GetParamMenuEntry`, so the repository had no resize path. FL's public SDK header says there is one:
`fp_plugclass.h` (the copy the repo already mirrors, `github.com/superjoebob/bendy_redo`, `src/fl/fp_plugclass.h`, line 715):
`const int FHD_EditorResized =2; // to notify the host that the editor (EditorHandle) has been resized`. The ids the repo already used (`FHD_ParamMenu` 0, `FHD_GetParamMenuEntry` 18) match that header. The header also has `FPD_WindowMinMax = 5`
("allows the plugin to set the editor window resizable"), which this work does not use.
So the editor does what the header describes: `SetWindowPos` on its own window, then `Host.Dispatcher(HostTag, Fhd.EditorResized, 0, 0)`, only when the width changed (Instruments <-> Presets keep the wide window and tell the host nothing).
**Not tried in real FL** (it needs the plugin installed with a UAC click; no agent can): that FL then fits its frame around the editor window. The header documents the call, not what FL does with it. If FL did not, the wide tabs would be clipped
at the old frame; the closest alternative would be a fixed wide window on every tab (changing the Emulator tab's size, which the brief forbids), so this was built the documented way.
What was checked on real windows: the test host's frame follows (`HostSim.EditorResized` -> `Win32Host.FitFrame`), and **the desktop app's standalone synth**, a real WinForms host that has the plugin's editor in a panel: with the new `SynthRack.EditorResized` / `FitEditorPanel`
the editor and its panel went 904 x 664 -> 1324 x 664 -> 1324 x 664 -> 904 x 664 on clicks on the three tabs (a script that finds the `BrokenNes2Editor` window of the running `BrokenNes.Windows.exe --synth` and clicks it with real messages; `BROKENNES_SYNTH_AUDIO=null`).

## Where the strip is, and why the Emulator tab does not change

The right column is full from y = 64 to the Reset row and the window may not change size, so the strip sits in the header band: x = 556 (the column's left edge), y = 36..56, resting on the header rule, clear of the About button (x >= 814) and of the badge text above it.
Verified: `bmpdiff` of editor captures from the **unmodified plugin** (built from the tree before any change) against the new plugin, with the strip rectangle (556, 36, 260 x 20) masked, in six states (default, SNES console, Sound chip dropdown open, Inputs window open, About window open, Genesis console):
**0 pixels differ outside the strip in all six** (1,060 pixels differ inside it in the five without a modal; the Inputs window covers the strip). The `ui` test adds: the 24 control rectangles of the default state equal the table captured from the unmodified DLL (`editor-golden`), and a round trip through both tabs leaves the Emulator tab's picture identical pixel for pixel.

## The contract I defined (what I assumed; `Ui/Contracts.cs` has the documentation)

- One `IInstrumentModelProvider` per (emulator, frozen game), created by `UiServices.Instruments(emulator)` when Instrument Runaway is on and dropped when it goes off or the emulator changes. The tab (not the model) says "Switch Instrument Runaway on to see the game's instruments", "Load a game in ROM mode, ...", and "The instrument model is not connected in this build of BrokenNes2." The model may say `Unavailable` (a reason shown instead of the list).
- Reading: `Version` (bumps on every change the tab can show; the tab caches until it changes), `Instruments` (name, detail, sounding flag), `Fields(i)` (`InstrumentField`: stable string id, name, `FieldKind` Enum / Range / BitField / Signed, min, max, value, one-line explanation, locked, optional enum names, optional group), `Blobs(i)` (`InstrumentBlob`: id, name, `BlobKind`, the stored bytes, the same decoded to 16-bit samples for the waveform, sample rate, loop start and end as sample indices or -1, editable, locked).
- Editing: `SetField`, `SetFieldLocked`, `SetBlobByte`, `SetBlobLocked`; corruption: `Randomise(i, strength 0..1)`, `Nudge(i, fieldId or null, -1 / +1)`, `CanUndo/CanRedo/Undo/Redo`, `HistoryText`, `SeedText`; `TakeForPreset(i)` returns a `PresetDraft` (name, console, source game, notes, an opaque `Payload`) that the tab hands to `IPresetLibrary.Add`; `PushIntoGame(i)`. Every action returns `ActionResult(Ok, Message)`; a refusal's reason is shown, never retried.
  The model owns the locks, the seed and the history (the tab holds only the strength setting, the chosen field for Nudge and the chosen blob). A locked field can still be edited by hand: the lock protects it from Randomise and Nudge only.
- `IPresetLibrary`: `Unavailable`, `Version`, `Search(text, console or null)` (`PresetSummary`), `Details(id)` (`PresetDetails`: name, console, category, source game, SHA-1 (reference only), tags, notes, field and blob counts, has note behaviour, origin), `Add`, `Load`, `Rename`, `Delete`, `Export(id, path)`, `Import(path)`, `Audition`. The tab asks for the file (standard Open / Save dialogs) and passes the path.
- Threading: every call is on the GUI thread, inside paint or a mouse / key handler; keep calls quick. An exception is caught and shown on the tab ("A model behind this tab failed").
- Registration: `UiBoot.RegisterInstrumentModel()` / `RegisterPresetLibrary()` partial methods, each implemented in its owner's own file (the example is in `Ui/UiBoot.cs`). Nothing registers a model in normal use today, so the tabs say "not connected" until `imodel` and `presets` merge.

## Verifiers (every one shown able to fail)

`FruityHost selftest <dll>` on the Native AOT DLL built the way `Plugin/build-dist.ps1` does (`dotnet publish ... --artifacts-path %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r6_ui`):

- **Full suite: 53 results, 52 pass, 1 warning (the QLQ core's octave, expected), 0 failed** (before this work 51: 50 + 1; the two new are `ui` and `ui-proof`). Run on the final build of this work (143 s).
- **`ui`** (about 10 s): 22 scenarios, each with its own plugin instance and editor, all driven by real `WM_LBUTTONDOWN` / `WM_MOUSEMOVE` / `WM_MOUSEWHEEL` / `WM_CHAR` / `WM_KEYDOWN` messages and the editor's own control record: `tabs` (sizes on each tab, the host told once per size change, the host's frame follows by exactly 420, the strip clear of About and of every control, modals open on a wide tab),
  `emulator-unchanged` (golden rectangles, round trip pixel-identical), `gate-direct`, `gate-runaway` (the three messages in order), `inst-list`, `inst-select`, `inst-fields-scroll`, `inst-slider`, `inst-stepper`, `inst-bits`, `inst-locks`, `inst-wave` (pixels counted in the wave colour and the marker colour in a screenshot of the editor),
  `inst-hex`, `inst-tools`, `inst-undo`, `inst-send-push`, `model-failure` (a model that throws while the tab paints, and one that throws on a write: the tab says so, the window stays usable and recovers), `presets-nolib`, `presets-list`, `presets-search`, `presets-buttons`, `keyboard-guard` (with every pad key simulated held, the pad gets nothing while a text field has the keyboard and gets it back after).
- **`ui-proof`** (9 - 14 s): 18 deliberate faults, each a single test of `UiMutate.Mask` at the place the check watches (zero in normal use; set through hook 499), and 2 negative controls (a check run where its answer is false): **20 of 20 caught, each by the intended assertion** (printed in the test's detail), e.g. "tabs: the Instruments tab is (904, 664), expected 1324 x 664" (window not resized), "tabs: the host was told 0 times ..." (host not told),
  "emulator-unchanged: control 1 is at 642,95, expected 642,94" (column one pixel lower), "inst-slider: clicking the middle of a 0..127 slider wrote 65, expected 64", "inst-stepper: plus made it 99", "inst-bits: setting bit 1 gave $21, expected $23", "inst-wave: ... drew 0 wave pixels", "inst-hex: typing FF over byte 0 wrote $80", "inst-undo: Undo gave value 102 ...",
  "presets-buttons: Export: the library heard 'Import ...'", "presets-search: searching 'bass': text '', 14 rows", "keyboard-guard: ... pad bits 4080", "inst-list / presets-list: two wheel notches down moved the list to row 0, expected 6", "gate-runaway: with Runaway off the Instruments tab says ''", "model-failure: a model that throws while the tab paints left '<none>' on the tab".
- The existing checks stay green: `editor`, `about`, `rom-editor`, `inputs`, `dpad`, `corruption`, `params` (no FL parameter added), `standalone-rack`.
- **Pixel comparison**: six states, 0 pixels differ outside the strip (above). **Real windows**: the standalone synth check above (the desktop app compiles: `dotnet build Windows/BrokenNes.Windows.csproj`, 0 errors).

## What is stubbed, what is not tested

- The instrument model and the preset library are the other agents'. The tabs run on stubs in the self-test only; in the product they say "not connected". **Nothing here proves the real models work in the tabs.**
- **Real FL: nothing was tried.** Open questions for the first FL session: (1) does FL's frame follow `FHD_EditorResized`; (2) does FL deliver `WM_CHAR` / `WM_KEYDOWN` to the editor after a click in a text or hex field (FL also plays notes from typed keys; the editor takes the keyboard with `SetFocus` and gives it back to the frame's window when the field lets go), and does it deliver the wheel; (3) is 1324 pixels too wide for the screens the user plays on (it fits 1366).
- No clipboard in the text field. The tab is not remembered when the window closes and opens again (it opens on Emulator). The text field has no selection except "all".
- The audio path is untouched by this work (no allocation or lock added; the UI runs on the GUI thread); the `realtime` check still reads 0 bytes. Nothing was listened to.

## Decisions

- Strip in the header band, not in the column: the column has no free row and the window may not change size; the cost is the strip being a little off the badge's line.
- `WideExtra` = 420 (the brief allowed 400 - 520): 1324 fits a 1366-wide screen with FL's frame; the panel is 752 wide (list 200, fields 538).
- Reset Console stays on the Emulator tab only (it restarts the emulator for every instance; not something to hit while corrupting an instrument). The left column (picture, status, Inputs / Corruption / Instrument Runaway) is on every tab.
- Fault switches in the shipped DLL rather than a second build: one DLL, repeatable proofs in 9 s; the cost is a handful of one-line tests of a static mask in widget code.
- `Kind.Ui` and ids >= 1000 for everything new, hits registered in the editor's own record so hover, the cursor, capture and `Test(30, id)` work unchanged.

## Findings outside the domain

- `Bn2Editor.OnMouse` sets `mouseX`/`mouseY` from every message's `lParam` before the wheel branch uses "the last known mouse position"; `WM_MOUSEWHEEL`'s lParam is the cursor in screen coordinates (0 from the test host), so the Emulator tab's wheel-over-a-slider / dropdown code (`HitAt(mouseX, mouseY)`) looks at the wrong point and does nothing useful.
  Left as it is because that tab must not change; the new tabs convert with `ScreenToClient`.
- `Plugin/README.md` said "50 results" at the top while the suite had 51; now 53.

## Next steps

1. The lead merges; `imodel` and `presets` implement the two interfaces in their own files (`UiBoot` partial methods) and rerun `ui` / `ui-proof` (the tabs' checks run against the stubs, so they stay green; add their own checks for the real models).
2. First FL session: the three open questions above.
3. If wanted: remember the tab per instance, a clipboard for the text field, a drag handle on the loop markers (they are fields of the instrument today).
