# Certifying Bogue :: BrokenNes 2 inside FL Studio 2026

`BrokenNes.FruityHost selftest` proves the plugin against a faithful *stand-in* for FL's host. This recipe proves it
inside the real thing: FL renders a project that uses the plugin, and the audio is measured against what the
notes should produce.

```
BrokenNes.Windows.exe --install-vst                                          # once per build; the picker, then one UAC prompt (or Config > Synthesizer Mode > Install to FL Studio...)
pwsh -File UAT\plugin\fl\fl-certify.ps1                                      # everything else; FL must be closed
```

(`fl-certify.ps1 -Install` installs through the FL-2026-only developer script `fl\install-to-fl.ps1`. `UAT\certify.ps1 -Only plugin-fl` runs it from the orchestrator.)

## The two other certifications in this folder (no FL Studio involved)

| Script | What it proves | How |
|---|---|---|
| `install-smoke.ps1` (`certify.ps1 -Only plugin-install`) | **Install to FL Studio** / `--install-vst`: detection of the real FL installs on the machine (excluding "FL Studio ASIO", Minihost, Shared), fresh / idempotent / replace-another-build installs, no partial file, two targets in one run, FL running from one installation refuses that install only, a non-FL folder, a missing DLL, "needs administrator" reported (an ACL-denied folder + `--elevated`), the picker opens and cancels with exit 2 | the real exe against FAKE FL folders under the work dir: no UAC, nothing in Program Files. **Not covered:** the UAC click and a copy into the real Program Files |
| `synth-smoke.ps1` (`certify.ps1 -Only plugin-synth`) | **the standalone synth** / `--synth`: the plugin's editor drawn in four tabs, the computer-keyboard piano (note, octave keys, switch off) reaching a rendered level, tab routing, saved settings written on close and restored, and the **restart handoff** emulator -> synth -> emulator through the real menus, plus a build with no plugin saying why and starting the emulator instead | the real windows through UI Automation and real key events, with `BROKENNES_SYNTH_AUDIO=null` (a documented switch: renders in real time into nothing). **Not covered:** the WASAPI handoff to a real playback device, MIDI hardware (the MIDI logic is `selftest standalone-midi`) |

Both build the desktop under `%LOCALAPPDATA%\VRUN_Nes_Dev_work` unless given `-Exe`, and both were also run against a **published** single-file build (the flavor
users download). Rule zero holds: only PIDs they started are stopped. The synth one presses real keys: keep the desktop quiet.

## What `fl-certify.ps1` does

1. **Fixture.** `FruityHost fixture fl-certify` writes the notes to play: pulse 1 / pulse 2 / triangle / noise notes,
   a slide up and a slide down, a triangle slide, a four-segment slide chain, and three velocities. The same fixture
   drives the host-side tests, so both worlds are judged by the same expectations.
2. **Project.** `make_template.py` derives an FL project from FL's own `Vocoder.flp` (shipped with FL, authored
   by the installed FL, so its format is exactly what that FL writes). It changes five things and leaves every other
   byte alone:
   - the first four generator channels become four `BrokenNes2` instances (one instance = one NES channel: pulse 1, pulse 2,
     triangle, noise; they share one emulator), each with the plugin's saved state for its channel;
   - the channels are routed to the master, and the template's mixer effects are removed (its Vocodex has its own synth);
   - the template's selected pattern (2) is replaced by ours (1), since a render plays the selected pattern;
   - pattern 1 gets the fixture's notes, each in the rack row of its instance. FL's own demo projects showed how it writes
     slides: ordinary notes have flags `0x4000`, a slide note `0x4008` and release byte 0, and it lies *inside* its parent note;
   - the playlist is one clip of pattern 1 from bar 1, the tempo is the fixture's.
3. **Render.** `FL64.exe /R /Ewav /F"<dir>"`: FL's command-line render. It loads the project (so loads the plugin),
   renders the song and exits by itself. The script refuses to start if FL is already open (the command would be
   handed to that instance), and only ever stops the FL process it started, on timeout.
4. **Analyse.** `FruityHost analyze` measures the WAV:
   - every steady note: frequency vs the pitch the NES chip produces for that key (the chip's own period grid,
     3 cents tolerance);
   - slides and chains: the pitch track vs the ideal glide (RMS 12 / max 35 cents), after finding FL's timing offset
     by search, so FL's real slide timing is *measured* and reported;
   - velocity: louder notes must be louder;
   - the gaps between notes must be silent (no stuck voices);
   - waveform restarts at period high-byte changes are excluded and counted, as in the host tests.

The report (`fl-certify.json`, `analysis.json`, the rendered `fl-render.wav`) goes to
`%LOCALAPPDATA%\VRUN_Nes_Dev_work\fl_certify`.

## Why it is built this way

- **No hand-made template.** An FLP can only be created by FL or by splicing a project FL wrote. flpkit
  (github.com/origami-research/fl-studio-mcp) established that FL refuses files re-serialized by writers that
  normalise the event stream, and accepts byte-level splices; `make_template.py` splices. The result is also
  parsed back by flpkit, an independent reader, as a check.
- **FL is the oracle.** Whether FL accepts the derived project and what it does with a slide is not assumed: the
  render shows it. If FL's slide shape or timing differs from the host stand-in's model, the analysis reports it
  (the best-fit timing offset is part of every slide row) and the model, not the plugin, is what changes.

## The plugin's name in FL

FL shows the plugin as **Bogue :: BrokenNes 2** (the name it reports), but the DLL and its folder stay `BrokenNes2`, and the generated project
refers to the plugin as `BrokenNes2`: FL identifies a native plugin by its file name. The first render after a rename confirms it (a project that
cannot find the plugin renders silence). `::` is not valid in a Windows file name, so if FL's preset menu ever misbehaves for this plugin, that name is the suspect.

## If it fails

| Symptom | Likely cause |
|---|---|
| `plugin installed in FL ...` fails | run `install-to-fl.ps1` (administrator); close FL first |
| `FL rendered and exited` fails (timeout) | FL is showing a dialog (a missing plugin: the DLL is not where FL scans, or FL was already running when it was installed) |
| `FL wrote a WAV` fails | FL could not load the project; start FL by hand and open `render\BrokenNes2_FLTest.flp` |
| everything sounds like something else (a synth that never stops) | the base project's own channels or mixer effects are still sounding: see the template notes above |
| everything silent | FL did not load the plugin; set `BROKENNES_PLUGIN_LOG` to a file and run again: no `CreatePlugInstance` line means FL never created it |
| pitch rows fail by a constant | FL's pitch convention differs (C5 = key 60 is assumed); the error column shows by how much |
| slide rows fail | see the `best timing offset` in the row; a large offset or high error means FL's glide differs from the model |

## Files

| File | Purpose |
|---|---|
| `fl/fl-certify.ps1` | the driver |
| `fl/install-to-fl.ps1` | the one elevated step (developer shortcut, FL 2026 only; the standard installer is the desktop app) |
| `install-smoke.ps1`, `synth-smoke.ps1` | the installer and the standalone synth (above) |
| `fl/make_template.py`, `fl/flplib.py` | derive the project (FLP event reader / writer) |
| `golden/cores-vrun-fix.json` | recorded picture hashes for the `cores` check of `certify.ps1` |
