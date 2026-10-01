# Certifying BrokenNes2 inside FL Studio 2026

`BrokenNes.FruityHost selftest` proves the plugin against a faithful *stand-in* for FL's host. This recipe proves it
inside the real thing: FL renders a project that uses the plugin, and the audio is measured against what the
notes should produce.

```
pwsh -File UAT\plugin\fl\install-to-fl.ps1 -Dll <...\BrokenNes2_x64.dll>      # once per build; administrator, one UAC prompt
pwsh -File UAT\plugin\fl\fl-certify.ps1                                      # everything else; FL must be closed
```

(`fl-certify.ps1 -Install` does both. `UAT\certify.ps1 -Only plugin-fl` runs it from the orchestrator.)

## What `fl-certify.ps1` does

1. **Fixture.** `FruityHost fixture fl-certify` writes the notes to play: pulse 1 / pulse 2 / triangle / noise notes,
   a slide up and a slide down, a triangle slide, a four-segment slide chain, and three velocities. The same fixture
   drives the host-side tests, so both worlds are judged by the same expectations.
2. **Project.** `make_template.py` derives an FL project from FL's own `Vocoder.flp` (shipped with FL, authored
   by the installed FL, so its format is exactly what that FL writes). It changes five things and leaves every other
   byte alone:
   - the first generator channel becomes `BrokenNes2`, and its saved state is the plugin's default state;
   - the channel is routed to the master, so no mixer effect colours the sound;
   - pattern 1 gets the fixture's notes: ordinary notes (flags `0x4000`), a slide-flagged note (`0x4008`) for each
     slide in a chain, the note colour in the colour byte;
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

## If it fails

| Symptom | Likely cause |
|---|---|
| `plugin installed in FL ...` fails | run `install-to-fl.ps1` (administrator); close FL first |
| `FL rendered and exited` fails (timeout) | FL is showing a dialog (a missing plugin: the DLL is not where FL scans, or FL was already running when it was installed) |
| `FL wrote a WAV` fails | FL could not load the project; start FL by hand and open `render\BrokenNes2_FLTest.flp` |
| everything silent | FL did not load the plugin; set `BROKENNES_PLUGIN_LOG` to a file and run again: no `CreatePlugInstance` line means FL never created it |
| pitch rows fail by a constant | FL's pitch convention differs (C5 = key 60 is assumed); the error column shows by how much |
| slide rows fail | see the `best timing offset` in the row; a large offset or high error means FL's glide differs from the model |

## Files

| File | Purpose |
|---|---|
| `fl/fl-certify.ps1` | the driver |
| `fl/install-to-fl.ps1` | the one elevated step |
| `fl/make_template.py`, `fl/flplib.py` | derive the project (FLP event reader / writer) |
| `golden/cores-vrun-fix.json` | recorded picture hashes for the `cores` check of `certify.ps1` |
